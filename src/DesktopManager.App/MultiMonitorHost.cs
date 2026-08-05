using System.Windows;
using DesktopManager.App.Windows;
using DesktopManager.Core.Models;
using DesktopManager.Core.Services;
using DesktopManager.Native;
using Serilog;

namespace DesktopManager.App;

/// <summary>M3-T3/T4：多屏宿主。每显示器一个 <see cref="IconLayerWindow"/>（定位到各自工作区），
/// 负责：①启动枚举显示器 + 按 <see cref="MonitorAssignment"/> 把 config 的 Fence/位置切分到各窗口；
/// ②桌面同步 diff 按归属分发（Added 投归属窗，无归属落主屏；Removed 广播）；
/// ③聚合持久化（所有窗口布局 + 离线屏孤儿配置，防抖 Save，替代单窗口时代的窗口内 Save）。
/// <para>孤儿语义：拔掉屏的 Fence/位置保留在 config（不进任何窗口、不渲染），插回后按持久 ID 原位恢复（T6）。</para>
/// <para>运行时归属真相源 = 窗口持有（fenced/loose），不维护 host 侧副本（避免双真相源）。</para></summary>
public sealed class MultiMonitorHost
{
    private readonly IConfigStore _store;
    private readonly Dictionary<string, IconLayerWindow> _windows = new(StringComparer.Ordinal);
    // M4：每屏壁纸窗口（与图标层 1:1 伴生，Z-order 在其正下方）+ 壁纸配置（单一真相源，孤儿含在内）。
    private readonly Dictionary<string, WallpaperWindow> _wallpaperWindows = new(StringComparer.Ordinal);
    private readonly List<WallpaperConfig> _wallpapers = new();
    // M5：显示组（组壁纸优先于独立壁纸；成员离线/删组自动回退）。
    private List<DisplayGroup> _displayGroups = new();

    // 孤儿（归属屏离线）：不进任何窗口，保存时原样带回（防布局数据丢失）。
    private readonly List<FenceConfig> _orphanFences = new();
    private readonly List<IconPosition> _orphanPositions = new();
    // 孤儿屏上的全部图标 path（loose + Fence 内容）：初始分发/Added 路由跳过，防误落主屏。
    private readonly HashSet<string> _orphanPaths = new(StringComparer.OrdinalIgnoreCase);

    // 桌面最新全集（增量维护）：拓扑重建给新增窗口补图标用。
    private readonly List<IconItem> _lastAll = new();
    // path → 归属屏（config 位置记录解析出的在线归属）：启动/重建分发 hint——
    // 窗口散落区未加载前 FindOwner 查不到，必须靠它把图标投到正确屏（否则全落主屏）。
    private Dictionary<string, string> _looseAssignHint = new(StringComparer.OrdinalIgnoreCase);

    // 防抖保存（从单窗口时代的 IconLayerWindow 上移）：任一窗口 LayoutChanged → 500ms 后聚合落盘。
    private readonly System.Threading.Timer _saveTimer;
    private readonly object _saveLock = new();
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);
    private volatile bool _savingDisabled;

    /// <summary>主屏窗口（新图标缺省归属 + ShellRestartWatcher 挂载点）。无主屏（畸形拓扑）退化首个窗口。</summary>
    public IconLayerWindow? PrimaryWindow { get; private set; }

    public IReadOnlyCollection<IconLayerWindow> Windows => _windows.Values;

    // M4：Z 看门狗——每 2s 幂等重锚「图标层置底」（壁纸窗 MakeClickThrough 自管置底）。
    // 真机：壁纸窗曾因未知机制浮到非 topmost 带顶（z=40）盖住普通窗口/任务栏；看门狗兜底。
    private System.Windows.Threading.DispatcherTimer? _zWatchdog;

    // M5-T4：组内视频漂移校正——2s 轮询，首成员为基准，|Δ|>0.5s 对齐。
    private System.Windows.Threading.DispatcherTimer? _videoSync;

    public MultiMonitorHost(IConfigStore store)
    {
        _store = store;
        _saveTimer = new System.Threading.Timer(_ => OnSaveTimerElapsed(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _zWatchdog = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _zWatchdog.Tick += (_, _) =>
        {
            foreach (var mon in _windows.Keys.ToList()) BottomPair(mon);
        };
        _zWatchdog.Start();

        _videoSync = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _videoSync.Tick += (_, _) => SyncGroupVideos();
        _videoSync.Start();
    }

    /// <summary>M5-T4：组内视频同步。基准 = 组成员序首个在线视频窗；其余 |Δ|&gt;0.5s → 对齐。
    /// 校正跳变可接受（壁纸语义非观影）；暂停态位置冻结天然一致，无需特判。</summary>
    private void SyncGroupVideos()
    {
        foreach (var g in _displayGroups)
        {
            if (string.IsNullOrWhiteSpace(g.WallpaperPath) || g.WallpaperKind != WallpaperKind.Video) continue;
            var wins = g.MonitorIds
                .Where(id => _wallpaperWindows.ContainsKey(id))
                .Select(id => _wallpaperWindows[id])
                .Where(w => w.IsVideo)
                .ToList();
            if (wins.Count < 2) continue;

            var master = wins[0];
            TimeSpan masterPos;
            try { masterPos = master.VideoPosition; }
            catch { continue; }
            foreach (var w in wins.Skip(1))
            {
                try
                {
                    var drift = (w.VideoPosition - masterPos).Duration();
                    if (drift > TimeSpan.FromSeconds(0.5))
                    {
                        w.VideoPosition = masterPos;
                        Log.Information("视频同步校正：{Mon} 漂移={Drift:F1}s → 对齐基准", w.MonitorId, drift.TotalSeconds);
                    }
                }
                catch { /* 窗口状态异常跳过，下轮再试 */ }
            }
        }
    }

    /// <summary>枚举显示器 → 加载 config → 按归属切分 → 每屏建窗口并 Show。
    /// 零显示器（理论不发生）抛异常，由 App 走回滚（RestoreExplorer）。</summary>
    public void Attach()
    {
        var monitors = MonitorEnumerator.Enumerate();
        if (monitors.Count == 0)
            throw new InvalidOperationException("未枚举到任何显示器，无法创建图标层");

        var config = _store.Load();
        _wallpapers.AddRange(config.Wallpapers); // M4：壁纸配置（离线屏的也保留 = 孤儿语义）
        _displayGroups = config.DisplayGroups.ToList(); // M5：显示组
        var online = monitors.Select(m => new MonitorRef(m.PersistentId, m.IsPrimary)).ToList();
        var fenceAssign = MonitorAssignment.FenceAssignments(config.Fences, online);
        var looseAssign = MonitorAssignment.LooseAssignments(config.IconPositions, online);

        foreach (var m in monitors)
        {
            var myFences = new List<FenceConfig>();
            var myPositions = new List<IconPosition>();
            foreach (var f in config.Fences)
            {
                if (fenceAssign[f.Id] == m.PersistentId) myFences.Add(f);
            }
            foreach (var p in config.IconPositions)
            {
                if (looseAssign[p.FilePath] == m.PersistentId) myPositions.Add(p);
            }

            var win = new IconLayerWindow(m, myFences, myPositions);
            win.Host = this; // M3-T5：跨屏拖拽迁移协调
            win.LayoutChanged += RequestSave;
            win.RequestBottom += () => BottomPair(m.PersistentId); // M4：Z-order 编排收归 host
            _windows[m.PersistentId] = win;
            win.Show();
            if (win.IsPrimary) PrimaryWindow ??= win;

            // M4-T2：壁纸窗伴生（Z-order 由 BottomPair 统一编排）。
            var wp = new WallpaperWindow(m);
            _wallpaperWindows[m.PersistentId] = wp;
            wp.Show();
            ApplyWallpaperTo(m.PersistentId);
            BottomPair(m.PersistentId);
        }
        PrimaryWindow ??= _windows.Values.First();

        // 分发 hint：config 位置记录解析出的在线归属（启动分发用，见 _looseAssignHint 注释）。
        _looseAssignHint = looseAssign
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase);

        // 孤儿：归属屏离线的 Fence/位置。数据保留（保存时带回），path 记入 _orphanPaths 防误分发。
        foreach (var f in config.Fences)
        {
            if (fenceAssign[f.Id] is not null) continue;
            _orphanFences.Add(f);
            foreach (var p in f.IconFilePaths) _orphanPaths.Add(p);
        }
        foreach (var p in config.IconPositions)
        {
            if (looseAssign[p.FilePath] is not null) continue;
            _orphanPositions.Add(p);
            _orphanPaths.Add(p.FilePath);
        }

        Log.Information("MultiMonitorHost：{Count} 个图标层窗口（{Monitors}），孤儿 Fence={OF} 位置={OP}",
            _windows.Count, string.Join(", ", monitors.Select(m => m.PersistentId)),
            _orphanFences.Count, _orphanPositions.Count);
    }

    /// <summary>M4-T5：设置某屏壁纸（右键菜单入口）：更新配置 + 即时应用 + 防抖落盘。</summary>
    public void SetWallpaper(string monitorId, string path)
    {
        _wallpapers.RemoveAll(w => string.Equals(w.MonitorId, monitorId, StringComparison.Ordinal));
        _wallpapers.Add(new WallpaperConfig
        {
            MonitorId = monitorId,
            Kind = WallpaperConfig.DetectKind(path),
            Path = path
        });
        ApplyWallpaperTo(monitorId);
        RequestSave();
        Log.Information("壁纸已设置：{Mon} ← {Path}", monitorId, path);
    }

    /// <summary>M4-T5：移除某屏壁纸（回退系统壁纸）：配置删除 + 窗口隐藏 + 落盘。</summary>
    public void RemoveWallpaper(string monitorId)
    {
        _wallpapers.RemoveAll(w => string.Equals(w.MonitorId, monitorId, StringComparison.Ordinal));
        ApplyWallpaperTo(monitorId); // 无配置 → 窗口 Hidden
        RequestSave();
        Log.Information("壁纸已移除：{Mon}", monitorId);
    }

    /// <summary>M4-T4：Governor 暂停所有壁纸播放（幂等，窗口内部自判）。</summary>
    public void PauseAllWallpapers()
    {
        foreach (var wp in _wallpaperWindows.Values) wp.Pause();
    }

    /// <summary>M4-T4：Governor 恢复所有壁纸播放（幂等）。</summary>
    public void ResumeAllWallpapers()
    {
        foreach (var wp in _wallpaperWindows.Values) wp.Resume();
    }

    /// <summary>M4：Z-order 编排——图标层置底，壁纸窗精确插到它正下方。
    /// 所有置底时机（图标层 ContentRendered/Activated/SourceInitialized 经 RequestBottom）都走这里，
    /// 杜绝两窗各自 SendToBottom 互踩。</summary>
    private void BottomPair(string monitorId)
    {
        // 图标层置底 + 壁纸窗插其正下方（幂等；看门狗每 2s 重锚，防壁纸窗浮高盖窗口/任务栏）。
        if (!_windows.TryGetValue(monitorId, out var win)) return;
        try
        {
            var iconH = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            WindowInterop.SendToBottom(iconH);
            if (_wallpaperWindows.TryGetValue(monitorId, out var wp))
            {
                var wpH = new System.Windows.Interop.WindowInteropHelper(wp).Handle;
                WindowInterop.PlaceBelow(wpH, iconH);
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warning(ex, "图标层置底失败（{Mon}）", monitorId);
        }
    }

    /// <summary>M4：壁纸窗可见性同步后重锚 Z-order（ShowWindow 会顶高 Z-order，真机：GIF 屏壁纸浮到 z=33 盖住普通窗口）。</summary>
    public void ReassertBottom(string monitorId) => BottomPair(monitorId);

    /// <summary>M5：显示组只读视图（设置窗口用）。</summary>
    public IReadOnlyList<DisplayGroup> Groups => _displayGroups;

    /// <summary>M5：设置窗口 commit：替换显示组 + 全部在线屏重渲染（组优先）+ 防抖落盘。</summary>
    public void SetDisplayGroups(IReadOnlyList<DisplayGroup> groups)
    {
        _displayGroups = groups.ToList();
        foreach (var mon in _windows.Keys.ToList()) ApplyWallpaperTo(mon);
        RequestSave();
    }

    /// <summary>M5：壁纸解析优先级：有壁纸的组（成员屏）&gt; 独立壁纸 &gt; null（隐藏）。返回命中组供画布计算。</summary>
    private (WallpaperConfig? Cfg, DisplayGroup? Group) ResolveWallpaper(string monitorId)
    {
        var g = _displayGroups.FirstOrDefault(g =>
            !string.IsNullOrWhiteSpace(g.WallpaperPath) && g.MonitorIds.Contains(monitorId));
        if (g is not null)
            return (new WallpaperConfig { MonitorId = monitorId, Kind = g.WallpaperKind, Path = g.WallpaperPath }, g);
        return (_wallpapers.FirstOrDefault(w => string.Equals(w.MonitorId, monitorId, StringComparison.Ordinal)), null);
    }

    /// <summary>M4：把配置里的壁纸应用到指定屏窗口（无配置 → 窗口隐藏，系统壁纸透出）。</summary>
    private void ApplyWallpaperTo(string monitorId)
    {
        if (!_wallpaperWindows.TryGetValue(monitorId, out var wp)) return;
        var (cfg, group) = ResolveWallpaper(monitorId);

        // M5-T3：组模式虚拟画布 = 组内在线成员 rect 的 bounding box；在线成员 <2 → null 降级单屏。
        IntRect? canvas = null;
        if (group is not null)
        {
            var onlineRects = MonitorEnumerator.Enumerate()
                .Where(m => group.MonitorIds.Contains(m.PersistentId))
                .Select(m => new IntRect(m.X, m.Y, m.X + m.Width, m.Y + m.Height))
                .ToList();
            if (onlineRects.Count >= 2) canvas = CrossScreenLayout.Canvas(onlineRects);
        }

        Log.Information("壁纸分发: {Mon} → cfg={Found} path={Path} canvas={Canvas}（独立 {N} 条 + 组 {G} 个）",
            monitorId, cfg is not null, cfg?.Path ?? "(null)", canvas is not null, _wallpapers.Count, _displayGroups.Count);
        wp.SetWallpaper(cfg, canvas);
    }

    /// <summary>M3-T6：拓扑变化重建（热插拔/分辨率/DPI/主屏切换，DisplayChangeWatcher 防抖后调，UI 线程）。
    /// 流程：①现状落盘（防关窗丢布局）→ ②重枚举 → ③关消失屏的窗口（布局已在盘上，插回恢复）
    /// → ④重算孤儿/归属 → ⑤存活屏重定位 → ⑥新增屏建窗加载布局 → ⑦刷新 PrimaryWindow。
    /// 重建以「当前内存布局聚合快照」为新 config 基线（含孤儿），不重读盘（盘上可能是旧防抖值）。</summary>
    public void RebuildToMatchTopology()
    {
        // 1. 现状落盘（含孤儿）：关窗前确保所有布局进盘 + 进快照。
        AppConfig snapshot;
        try
        {
            snapshot = BuildAggregatedConfig();
            lock (_saveLock)
            {
                if (!_savingDisabled) _store.Save(snapshot);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RebuildToMatchTopology：重建前保存失败，放弃重建（保现状）");
            return;
        }

        var monitors = MonitorEnumerator.Enumerate();
        if (monitors.Count == 0) return; // 枚举异常/全灭：不动现有窗口，等下一次事件

        var online = monitors.Select(m => new MonitorRef(m.PersistentId, m.IsPrimary)).ToList();
        var liveIds = new HashSet<string>(monitors.Select(m => m.PersistentId), StringComparer.Ordinal);

        // 2. 关消失屏的窗口（其布局已含在 snapshot → 插回时按持久 ID 恢复）。
        foreach (var goneId in _windows.Keys.Where(k => !liveIds.Contains(k)).ToList())
        {
            var w = _windows[goneId];
            w.LayoutChanged -= RequestSave;
            w.Close();
            _windows.Remove(goneId);
            if (_wallpaperWindows.Remove(goneId, out var wpGone)) wpGone.Close();
            Log.Information("拓扑重建：显示器离线，关闭图标层窗口 {Id}", goneId);
        }

        // 3. 以 snapshot 为新基线重算孤儿 + 每屏归属（孤儿集合整体重置）。
        _orphanFences.Clear(); _orphanPositions.Clear(); _orphanPaths.Clear();
        var fenceAssign = MonitorAssignment.FenceAssignments(snapshot.Fences, online);
        var looseAssign = MonitorAssignment.LooseAssignments(snapshot.IconPositions, online);
        foreach (var f in snapshot.Fences)
        {
            if (fenceAssign[f.Id] is not null) continue;
            _orphanFences.Add(f);
            foreach (var p in f.IconFilePaths) _orphanPaths.Add(p);
        }
        foreach (var p in snapshot.IconPositions)
        {
            if (looseAssign[p.FilePath] is not null) continue;
            _orphanPositions.Add(p);
            _orphanPaths.Add(p.FilePath);
        }
        _looseAssignHint = looseAssign
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase);
        var myFencesByMon = monitors.ToDictionary(m => m.PersistentId, _ => new List<FenceConfig>());
        var myPositionsByMon = monitors.ToDictionary(m => m.PersistentId, _ => new List<IconPosition>());
        foreach (var f in snapshot.Fences)
        {
            var a = fenceAssign[f.Id];
            if (a is not null) myFencesByMon[a].Add(f);
        }
        foreach (var p in snapshot.IconPositions)
        {
            var a = looseAssign[p.FilePath];
            if (a is not null) myPositionsByMon[a].Add(p);
        }

        // 4. 存活屏：仅重定位（本地坐标不换算）。
        foreach (var m in monitors)
        {
            if (_windows.TryGetValue(m.PersistentId, out var win)) win.RepositionTo(m);
            if (_wallpaperWindows.TryGetValue(m.PersistentId, out var wpAlive)) wpAlive.RepositionTo(m);
        }

        // 5. 新增屏：建窗 + 加载该屏布局（拔线插回 = 走这里原位恢复），并按归属补发桌面图标。
        var newWindows = new List<IconLayerWindow>();
        foreach (var m in monitors)
        {
            if (_windows.ContainsKey(m.PersistentId)) continue;
            var win = new IconLayerWindow(m, myFencesByMon[m.PersistentId], myPositionsByMon[m.PersistentId]);
            win.Host = this;
            win.LayoutChanged += RequestSave;
            win.RequestBottom += () => BottomPair(m.PersistentId);
            _windows[m.PersistentId] = win;
            win.Show();
            // M4：新屏壁纸窗伴生（插回屏的壁纸按 config 原位恢复）。
            var wp = new WallpaperWindow(m);
            _wallpaperWindows[m.PersistentId] = wp;
            wp.Show();
            ApplyWallpaperTo(m.PersistentId);
            BottomPair(m.PersistentId);
            newWindows.Add(win);
            Log.Information("拓扑重建：显示器上线，新建图标层窗口 {Id}", m.PersistentId);
        }
        if (newWindows.Count > 0 && _lastAll.Count > 0)
        {
            // Distribute 只投 target 窗口；存量图标在旧窗已有，FindOwner 命中旧窗 → 不会重复投。
            Distribute(_lastAll, newWindows);
        }

        // 6. 主屏可能切换：PrimaryWindow 指向当前主屏窗口（存量 _allItems/IsPrimary 不搬，backlog）。
        var primaryId = monitors.FirstOrDefault(m => m.IsPrimary)?.PersistentId;
        if (primaryId is not null && _windows.TryGetValue(primaryId, out var pw)) PrimaryWindow = pw;
        else PrimaryWindow = _windows.Values.FirstOrDefault();

        // 7. 布局已随 snapshot 落盘；重建本身不触发 RequestSave（无布局语义变化）。
        Log.Information("拓扑重建完成：{Count} 个窗口，孤儿 Fence={OF} 位置={OP}",
            _windows.Count, _orphanFences.Count, _orphanPositions.Count);
    }

    /// <summary>启动全量分发：按归属把快照切给各窗口（并记 _lastAll 供重建补发）。</summary>
    public void ApplyInitialSnapshot(IReadOnlyList<IconItem> all)
    {
        _lastAll.Clear();
        _lastAll.AddRange(all);
        Distribute(all, _windows.Values);
    }

    /// <summary>按归属分发到目标窗口：Fence/散落持有（FindOwner）→ 该窗；config 位置 hint → 该窗；
    /// 都没有 → 主屏（新图标缺省）。孤儿 path 跳过（不渲染，插回恢复）。</summary>
    private void Distribute(IReadOnlyList<IconItem> all, IEnumerable<IconLayerWindow> targets)
    {
        var groups = targets.ToDictionary(w => w, _ => new List<IconItem>());
        foreach (var item in all)
        {
            if (_orphanPaths.Contains(item.FilePath)) continue;
            var owner = FindOwner(item.FilePath);
            IconLayerWindow target;
            if (owner is not null) target = owner;
            else if (_looseAssignHint.TryGetValue(item.FilePath, out var mon) && _windows.TryGetValue(mon, out var hw))
                target = hw;
            else target = PrimaryWindow!;
            if (groups.TryGetValue(target, out var list)) list.Add(item);
        }
        foreach (var (win, items) in groups)
            win.ApplySnapshot(items);
    }

    /// <summary>增量分发（sync.Changed）。
    /// Removed 广播所有窗口（窗口无该 path 则 reconcile 自然 no-op，避免归属竞态）；
    /// Added 按归属投单窗：Fence/散落持有窗，无归属落主屏。</summary>
    public void Dispatch(DesktopDiff diff)
    {
        // 维护桌面全集快照（拓扑重建补发用）：倒序按 path 删，再追加 Added。
        if (diff.Removed.Count > 0)
        {
            var removedPaths = new HashSet<string>(diff.Removed.Select(r => r.FilePath), StringComparer.OrdinalIgnoreCase);
            for (int i = _lastAll.Count - 1; i >= 0; i--)
                if (removedPaths.Contains(_lastAll[i].FilePath)) _lastAll.RemoveAt(i);
        }
        _lastAll.AddRange(diff.Added);

        if (diff.Removed.Count > 0)
        {
            var removedOnly = new DesktopDiff(Array.Empty<IconItem>(), diff.Removed);
            foreach (var w in _windows.Values) w.ApplyDiff(removedOnly);
        }

        if (diff.Added.Count == 0) return;
        var groups = new Dictionary<IconLayerWindow, List<IconItem>>();
        foreach (var item in diff.Added)
        {
            if (_orphanPaths.Contains(item.FilePath)) continue;
            var owner = FindOwner(item.FilePath) ?? PrimaryWindow;
            if (owner is null) continue;
            if (!groups.TryGetValue(owner, out var list)) groups[owner] = list = new List<IconItem>();
            list.Add(item);
        }
        foreach (var (w, items) in groups)
            w.ApplyDiff(new DesktopDiff(items, Array.Empty<IconItem>()));
    }

    // ---------- M3-T5：跨屏拖拽迁移 ----------

    /// <summary>图标跨屏迁移：path 属 source 窗口（Fence 或散落），拖落在 target 空白 → 迁到 target 散落区 Drop 位置。
    /// source==target 或无归属时 no-op（同窗场景由窗口内部 Drop 分支处理）。</summary>
    public void TransferLoose(string path, IconLayerWindow target, System.Windows.Point pos)
    {
        var source = FindOwner(path);
        if (source is null || source == target) return;
        var item = source.ExportIcon(path);
        if (item is null) return; // 文件已删等：不迁幽灵图标
        target.ImportLoose(item, pos);
        Log.Information("跨屏迁移图标：{Path} → {Monitor}", path, target.MonitorId);
    }

    /// <summary>Fence 跨屏迁移：source 静默移除 → target 重建（归属图标随迁，X/Y=Drop 位置）。
    /// 同窗拖放 = 仅换位置（MoveFence）。</summary>
    public void TransferFence(string fenceId, IconLayerWindow target, System.Windows.Point pos)
    {
        var source = _windows.Values.FirstOrDefault(w => w.ContainsFence(fenceId));
        if (source is null) return;
        if (source == target)
        {
            target.MoveFence(fenceId, pos);
            return;
        }
        var cfg = source.ExportFence(fenceId);
        if (cfg is null) return;
        target.ImportFence(cfg with { X = pos.X, Y = pos.Y });
        Log.Information("跨屏迁移 Fence：{Id}（{Title}）→ {Monitor}", fenceId, cfg.Title, target.MonitorId);
    }

    /// <summary>运行时归属查询：哪个窗口持有该 path（Fence 归属或散落区）。</summary>
    private IconLayerWindow? FindOwner(string path)
    {
        foreach (var w in _windows.Values)
            if (w.ContainsFenced(path) || w.ContainsLoose(path)) return w;
        return null;
    }

    // ---------- 聚合持久化（T4） ----------

    /// <summary>任一窗口布局变更 → 防抖聚合保存。</summary>
    private void RequestSave()
    {
        if (_savingDisabled) return;
        lock (_saveLock)
        {
            _saveTimer.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Timer 回调（ThreadPool）：Dispatcher.Invoke 聚合（读 UI 状态）→ Save（文件 IO）。</summary>
    private void OnSaveTimerElapsed()
    {
        if (_savingDisabled) return;
        try
        {
            var dispatcher = Application.Current.Dispatcher;
            AppConfig appConfig = dispatcher.CheckAccess()
                ? BuildAggregatedConfig()
                : (AppConfig)dispatcher.Invoke(new Func<AppConfig>(BuildAggregatedConfig));

            lock (_saveLock)
            {
                if (_savingDisabled) return;
                _store.Save(appConfig);
            }
        }
        catch (Exception ex)
        {
            // 持久化失败不崩 UI；下次变更再触发重试。
            Log.Warning(ex, "MultiMonitorHost 防抖保存失败");
        }
    }

    /// <summary>聚合所有窗口布局（各窗口已打 MonitorId 戳）+ 孤儿配置（离线屏数据不丢）。UI 线程。</summary>
    private AppConfig BuildAggregatedConfig()
    {
        var fences = new List<FenceConfig>(_orphanFences);
        var positions = new List<IconPosition>(_orphanPositions);
        foreach (var w in _windows.Values)
        {
            var (f, p) = w.BuildLayout();
            fences.AddRange(f);
            positions.AddRange(p);
        }
        // M4/M5：壁纸配置 + 显示组整体带回（含离线屏孤儿——从不过滤）。
        return new AppConfig
        {
            Fences = fences,
            IconPositions = positions,
            Wallpapers = _wallpapers.ToList(),
            DisplayGroups = _displayGroups.ToList()
        };
    }

    /// <summary>立即聚合保存（不等防抖）。OnExit 调用；随后 CloseAll。</summary>
    public void SaveAllNow()
    {
        _savingDisabled = true;
        try { _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        catch { /* ObjectDisposedException 忽略 */ }

        try
        {
            var appConfig = BuildAggregatedConfig(); // OnExit 在 UI 线程
            lock (_saveLock)
            {
                _store.Save(appConfig);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MultiMonitorHost 退出保存失败");
        }
    }

    /// <summary>关闭所有窗口（OnExit，SaveAllNow 之后）。</summary>
    public void CloseAll()
    {
        foreach (var w in _windows.Values)
        {
            w.LayoutChanged -= RequestSave;
            w.Close();
        }
        _windows.Clear();
        foreach (var wp in _wallpaperWindows.Values) wp.Close();
        _wallpaperWindows.Clear();
        _zWatchdog?.Stop();
        _videoSync?.Stop();
    }
}
