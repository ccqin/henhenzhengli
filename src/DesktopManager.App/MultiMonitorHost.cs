using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using DesktopManager.App.Services;
using DesktopManager.Core.Models;
using DesktopManager.Core.Services;
using DesktopManager.Ipc;
using DesktopManager.Native;
using Serilog;

namespace DesktopManager.App;

/// <summary>图标层子进程运行时状态：IPC 通道 + 最新布局缓存（LayoutChanged 上报维护，
/// 作为运行时归属真相源 + 聚合持久化数据源）。</summary>
internal sealed class IconChild
{
    public required ChildProcessManager Player { get; init; }
    public required MonitorInfo Monitor { get; init; }
    public List<FenceConfig> Fences { get; set; } = [];
    public List<IconPosition> Positions { get; set; } = [];

    public bool OwnsPath(string path) =>
        Fences.Any(f => f.IconFilePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        || Positions.Any(p => string.Equals(p.FilePath, path, StringComparison.OrdinalIgnoreCase));

    public bool ContainsFence(string fenceId) => Fences.Any(f => f.Id == fenceId);
}

/// <summary>M6 子进程架构：壁纸 + 图标层都是独立子进程（Ready 上报 hwnd → SetParent 到 WorkerW）。
/// 本类职责：①启动/管理子进程生命周期；②config 归属切分下发（SetFences/SetIcons/ApplyDiff）；
/// ③跨屏操作中转（拖拽迁移/全局清选中）；④聚合持久化（子进程 LayoutChanged 上报 + 孤儿配置，防抖落盘）。
/// <para>孤儿语义：拔掉屏的 Fence/位置保留在 config（不进任何子进程、不渲染），插回后按持久 ID 原位恢复。</para></summary>
public sealed class MultiMonitorHost
{
    private readonly IConfigStore _store;
    private readonly Dictionary<string, IconChild> _iconChildren = new(StringComparer.Ordinal);
    // M6：壁纸子进程（每屏一个）+ 壁纸配置（单一真相源，孤儿含在内）。
    private readonly Dictionary<string, ChildProcessManager> _wallpaperPlayers = new(StringComparer.Ordinal);
    private readonly List<WallpaperConfig> _wallpapers = new();
    // M5：显示组（组壁纸优先于独立壁纸；成员离线/删组自动回退）。
    private List<DisplayGroup> _displayGroups = new();
    // M6 美化：外观（图标尺寸档 + 标签风格），配置加载、SetAppearance 下发子进程。
    private AppearanceConfig _appearance = new();
    // M6 美化：右键菜单配置（内置开关 + 自定义项），SetMenu 下发子进程。
    private MenuConfig _menu = new();

    // 孤儿（归属屏离线）：不进任何子进程，保存时原样带回（防布局数据丢失）。
    private readonly List<FenceConfig> _orphanFences = new();
    private readonly List<IconPosition> _orphanPositions = new();
    // 孤儿屏上的全部图标 path（loose + Fence 内容）：初始分发/Added 路由跳过，防误落主屏。
    private readonly HashSet<string> _orphanPaths = new(StringComparer.OrdinalIgnoreCase);

    // 桌面最新全集（增量维护）：拓扑重建给新增子进程补图标用。
    private readonly List<IconItem> _lastAll = new();
    // path → 归属屏（config 位置记录解析出的在线归属）：启动分发 hint。
    private Dictionary<string, string> _looseAssignHint = new(StringComparer.OrdinalIgnoreCase);

    // M6 拆分：持久化委托给 PersistenceService（防抖/立即保存）。
    private readonly Services.PersistenceService _persistence;

    // 跨屏迁移中转的 pending 槽（用户操作低频，单槽 + 后到覆盖足够）。
    private (string TargetMonitor, string? Path, string? FenceId, double X, double Y)? _pendingImport;

    /// <summary>主屏持久 ID（新图标缺省归属）。</summary>
    public string? PrimaryMonitorId { get; private set; }

    // M5-T4 恢复（M6 IPC 版）：组内视频漂移校正——2s 轮询，首成员位置为基准，|Δ|>0.5s 对齐。
    private System.Windows.Threading.DispatcherTimer? _videoSync;
    private readonly Dictionary<string, double> _videoPos = new(StringComparer.Ordinal); // monitorId → 最新位置 ms

    public MultiMonitorHost(IConfigStore store)
    {
        _store = store;
        _persistence = new Services.PersistenceService(store, BuildAggregatedConfig);
        _videoSync = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _videoSync.Tick += (_, _) => SyncGroupVideos();
        _videoSync.Start();
        // Z 看门狗已移除（2026-08-19）：owner=DefView 约束 + WM_MOUSEACTIVATE/MA_NOACTIVATE 双保险
        // 已根治浮高源；看门狗反而在交互（右键菜单等）期间误判重锚造成闪屏。
    }

    /// <summary>枚举显示器 → 加载 config → 按归属切分 → 每屏启动壁纸 + 图标层子进程并挂 WorkerW。
    /// 零显示器（理论不发生）抛异常，由 App 走回滚（RestoreExplorer）。</summary>
    public void Attach() => AttachCore(_store.Load());

    /// <summary>M6：explorer 重启后重建所有子进程（旧 WorkerW 已销毁，子窗口随之失效）。
    /// 以当前布局缓存为新基线（先落盘），重启全部子进程并重新挂新 WorkerW。</summary>
    public void ReattachAll()
    {
        var snapshot = BuildAggregatedConfig();
        foreach (var c in _iconChildren.Values) c.Player.Stop();
        _iconChildren.Clear();
        foreach (var p in _wallpaperPlayers.Values) p.Stop();
        _wallpaperPlayers.Clear();
        _orphanFences.Clear(); _orphanPositions.Clear(); _orphanPaths.Clear();
        _wallpapers.Clear(); _displayGroups = new List<DisplayGroup>();
        AttachCore(snapshot);
        if (_lastAll.Count > 0) ApplyInitialSnapshot(_lastAll);
    }

    private void AttachCore(AppConfig config)
    {
        var monitors = MonitorEnumerator.Enumerate();
        if (monitors.Count == 0)
            throw new InvalidOperationException("未枚举到任何显示器，无法创建图标层");

        _wallpapers.AddRange(config.Wallpapers);
        _displayGroups = config.DisplayGroups.ToList();
        _appearance = config.Appearance;
        _menu = config.Menu;
        // B3：开机自启（注册表与 config 对账，config 为真相源）
        if (config.AutoStart != DesktopManager.Native.AutoStart.IsEnabled())
        {
            try { DesktopManager.Native.AutoStart.SetEnabled(config.AutoStart); }
            catch (Exception ex) { Log.Warning(ex, "自启注册表设置失败"); }
        }
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

            // 壁纸先挂（WorkerW 内 sibling 低序），图标层后挂（其上）。
            StartWallpaperPlayer(m);
            StartIconPlayer(m, myFences, myPositions);
            if (m.IsPrimary) PrimaryMonitorId ??= m.PersistentId;
        }
        PrimaryMonitorId ??= monitors[0].PersistentId;

        // 分发 hint：config 位置记录解析出的在线归属（启动分发用）。
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

        Log.Information("MultiMonitorHost(M6)：{Count} 屏子进程就绪（{Monitors}），孤儿 Fence={OF} 位置={OP}",
            _iconChildren.Count, string.Join(", ", monitors.Select(m => m.PersistentId)),
            _orphanFences.Count, _orphanPositions.Count);
    }

    // ---------- 子进程启动 ----------

    /// <summary>M6：启动图标层子进程并挂到 WorkerW（壁纸之上）。挂载后立即下发本屏 Fence 子集。</summary>
    private void StartIconPlayer(MonitorInfo m, List<FenceConfig> fences, List<IconPosition> positions)
    {
        try
        {
            var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopManager.Player.Icons.exe");
            var player = new ChildProcessManager(m.PersistentId);
            player.MessageReceived += msg => Application.Current?.Dispatcher.BeginInvoke(() => OnIconMessage(m.PersistentId, msg));
            player.Exited += code => Application.Current?.Dispatcher.BeginInvoke(() => OnIconChildExited(m.PersistentId, code));
            var args = $"--monitor-id {m.PersistentId} --device {m.DeviceName} --primary {(m.IsPrimary ? 1 : 0)} " +
                       $"--x {m.X} --y {m.Y} --w {m.Width} --h {m.Height} " +
                       $"--work-x {m.WorkX} --work-y {m.WorkY} --work-w {m.WorkWidth} --work-h {m.WorkHeight}";
            var hwnd = player.StartAsync(exe, args).GetAwaiter().GetResult();
            DesktopLayerHost.AttachToDesktop(hwnd, m.WorkX, m.WorkY, m.WorkWidth, m.WorkHeight, iconLayer: true);
            _iconChildren[m.PersistentId] = new IconChild { Player = player, Monitor = m, Fences = fences, Positions = positions };
            player.Send(new Show());
            player.Send(new SetAppearance { IconSize = _appearance.IconSize, LabelStyle = _appearance.LabelStyle });
            player.Send(new SetMenu
            {
                ShowOpen = _menu.ShowOpen, ShowRename = _menu.ShowRename,
                ShowDelete = _menu.ShowDelete, ShowLocate = _menu.ShowLocate,
                ShowSystemMenu = _menu.ShowSystemMenu,
                CustomItems = _menu.CustomItems.Select(c => new CustomItemDto
                { Name = c.Name, Command = c.Command, Extensions = c.Extensions }).ToList(),
                SystemMenuHidden = _menu.SystemMenuHidden.ToList(),
            });
            player.Send(new SetFences { Fences = fences.Select(ToDto).ToList() });
            BottomPair(m.PersistentId); // 图标层置底 + 壁纸插其正下方
        }
        catch (Exception ex)
        {
            Log.Error(ex, "图标层子进程启动失败：{Mon}", m.PersistentId);
            if (_iconChildren.Remove(m.PersistentId, out var dead)) dead.Player.Dispose();
        }
    }

    /// <summary>Z 序编排：图标层置底 + 壁纸窗插其正下方（M5 BottomPair， hwnd 来自子进程 Ready 上报）。</summary>
    private void BottomPair(string monitorId)
    {
        try
        {
            if (_iconChildren.TryGetValue(monitorId, out var icon))
            {
                var iconH = (IntPtr)icon.Player.Hwnd;
                WindowInterop.SendToBottom(iconH);
                if (_wallpaperPlayers.TryGetValue(monitorId, out var wp))
                    WindowInterop.PlaceBelow((IntPtr)wp.Hwnd, iconH);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BottomPair 失败（{Mon}）", monitorId);
        }
    }

    /// <summary>M6：图标层子进程异常退出 → 重启 + 恢复 Fences + 补发全量图标。</summary>
    private void OnIconChildExited(string monitorId, int code)
    {
        if (code == 0) return;
        if (!_iconChildren.TryGetValue(monitorId, out var child)) return; // 正常 Stop 已移除
        Log.Warning("图标层子进程异常退出（code={Code}），重启：{Mon}", code, monitorId);
        var fences = child.Fences;
        var positions = child.Positions;
        _iconChildren.Remove(monitorId);
        var live = MonitorEnumerator.Enumerate().FirstOrDefault(x => x.PersistentId == monitorId);
        if (live is null) return;
        // fences 以缓存为准（含运行期变更）；positions 仅作排位缓存，图标全集走 _lastAll 补发。
        StartIconPlayer(live, fences, positions);
        if (_iconChildren.TryGetValue(monitorId, out var reborn) && _lastAll.Count > 0)
        {
            reborn.Player.Send(new SetIcons { Items = SplitFor(monitorId, _lastAll) });
        }
    }

    /// <summary>M6：启动壁纸子进程并挂到 WorkerW。异常不抛（单屏失败不影响其余屏）。</summary>
    private void StartWallpaperPlayer(MonitorInfo m)
    {
        try
        {
            var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopManager.Player.Wallpaper.exe");
            var player = new ChildProcessManager(m.PersistentId);
            player.MessageReceived += msg => Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                switch (msg)
                {
                    case DesktopManager.Ipc.Error err:
                        Log.Error("壁纸子进程[{Mon}]：{Msg}", m.PersistentId, err.Message);
                        break;
                    case VideoPositionReport vr:
                        _videoPos[m.PersistentId] = vr.PositionMs; // 组内对齐用（SyncGroupVideos 消费）
                        break;
                }
            });
            player.Exited += code =>
            {
                if (code != 0 && _wallpaperPlayers.TryGetValue(m.PersistentId, out var cur) && cur == player)
                {
                    Log.Warning("壁纸子进程异常退出（code={Code}），重启：{Mon}", code, m.PersistentId);
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (_wallpaperPlayers.Remove(m.PersistentId))
                        {
                            var live = MonitorEnumerator.Enumerate().FirstOrDefault(x => x.PersistentId == m.PersistentId);
                            if (live is not null) StartWallpaperPlayer(live);
                        }
                    });
                }
            };
            var args = $"--monitor-x {m.X} --monitor-y {m.Y} --monitor-w {m.Width} --monitor-h {m.Height}";
            var hwnd = player.StartAsync(exe, args).GetAwaiter().GetResult();
            // 底部 2px 缝（M4 真机教训回归修复）：顶层全屏无边框窗会触发 shell 全屏检测 →
            // 任务栏被自动隐藏（副屏开窗时触发）。-2px 破检测，缝藏在任务栏后不可见。
            DesktopLayerHost.AttachToDesktop(hwnd, m.X, m.Y, m.Width, m.Height - 2);
            _wallpaperPlayers[m.PersistentId] = player;
            player.Send(new Show());
            ApplyWallpaperTo(m.PersistentId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "壁纸子进程启动失败：{Mon}", m.PersistentId);
            if (_wallpaperPlayers.Remove(m.PersistentId, out var dead)) dead.Dispose();
        }
    }

    // ---------- 图标层子进程消息处理 ----------

    private void OnIconMessage(string monitorId, IpcMessage msg)
    {
        switch (msg)
        {
            case LayoutChanged lc:
                if (_iconChildren.TryGetValue(monitorId, out var child))
                {
                    child.Fences = lc.Fences.Select(f => FromDto(f) with { MonitorId = monitorId }).ToList();
                    child.Positions = lc.Positions.Select(p => new IconPosition(p.Path, p.X, p.Y, monitorId)).ToList();
                    Services.LogDb.Audit("fence", "layout", $"{child.Fences.Count} 收纳盒 / {child.Positions.Count} 散落图标", monitorId);
                    RequestSave();
                }
                break;

            case FenceAction fa:
                Services.LogDb.Audit("fence", fa.Action, fa.Title, monitorId);
                break;

            case IconAction ia:
                Services.LogDb.Audit("icon", ia.Action, ia.Detail is { Length: > 0 } ? ia.Detail : ia.Path, monitorId);
                break;

            case ClearSelectionExcept cs:
                foreach (var (mon, c) in _iconChildren)
                    if (mon != cs.MonitorId) c.Player.Send(new ClearSelection());
                break;

            case TransferLooseReq req:
                // 主进程中转：源屏（缓存归属）导出 → 目标屏导入。
                var owner = FindOwnerMonitor(req.Path);
                if (owner is null || owner == req.TargetMonitorId) break;
                Services.LogDb.Audit("icon", "cross-screen", req.Path, req.TargetMonitorId);
                _pendingImport = (req.TargetMonitorId, req.Path, null, req.X, req.Y);
                _iconChildren[owner].Player.Send(new ExportIcon { Path = req.Path });
                break;

            case TransferFenceReq req:
                var fenceOwner = _iconChildren.FirstOrDefault(kv => kv.Value.ContainsFence(req.FenceId));
                if (fenceOwner.Key is null || fenceOwner.Key == req.TargetMonitorId) break;
                Services.LogDb.Audit("fence", "cross-screen", req.FenceId, req.TargetMonitorId);
                _pendingImport = (req.TargetMonitorId, null, req.FenceId, req.X, req.Y);
                fenceOwner.Value.Player.Send(new ExportFence { FenceId = req.FenceId });
                break;

            case ExportIconData data:
                if (!data.Found || _pendingImport is not { } pi || pi.Path != data.Path) break;
                if (_iconChildren.TryGetValue(pi.TargetMonitor, out var target1))
                    target1.Player.Send(new ImportIcon { Path = data.Path, Name = data.Name, X = pi.X, Y = pi.Y });
                _pendingImport = null;
                break;

            case ExportFenceData data:
                if (!data.Found || data.Fence is null || _pendingImport is not { } pf || pf.FenceId != data.Fence.Id) break;
                if (_iconChildren.TryGetValue(pf.TargetMonitor, out var target2))
                    target2.Player.Send(new ImportFence { Fence = data.Fence, X = pf.X, Y = pf.Y });
                _pendingImport = null;
                break;

            case IconOpened io:
                Log.Information("图标已打开：{Path}", io.Path);
                break;

            case Error err:
                Log.Warning("图标层子进程错误[{Mon}]：{Msg}", monitorId, err.Message);
                break;
        }
    }

    private string? FindOwnerMonitor(string path)
    {
        foreach (var (mon, child) in _iconChildren)
            if (child.OwnsPath(path)) return mon;
        return null;
    }

    // ---------- 壁纸 API（设置窗口 / Governor 用） ----------

    /// <summary>M4-T5：设置某屏壁纸：更新配置 + 即时应用 + 防抖落盘。</summary>
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
        Services.LogDb.Audit("wallpaper", "set", path, monitorId);
        Log.Information("壁纸已设置：{Mon} ← {Path}", monitorId, path);
    }

    /// <summary>M4-T5：移除某屏壁纸（回退系统壁纸）。</summary>
    public void RemoveWallpaper(string monitorId)
    {
        _wallpapers.RemoveAll(w => string.Equals(w.MonitorId, monitorId, StringComparison.Ordinal));
        ApplyWallpaperTo(monitorId);
        RequestSave();
        Services.LogDb.Audit("wallpaper", "remove", "", monitorId);
        Log.Information("壁纸已移除：{Mon}", monitorId);
    }

    /// <summary>组内视频对齐：有视频壁纸的组，以成员序首个为基准，其余 |Δ|&gt;0.5s → SetVideoPosition。</summary>
    private void SyncGroupVideos()
    {
        foreach (var g in _displayGroups)
        {
            if (string.IsNullOrWhiteSpace(g.WallpaperPath) || g.WallpaperKind != WallpaperKind.Video) continue;
            var members = g.MonitorIds.Where(_wallpaperPlayers.ContainsKey).ToList();
            if (members.Count < 2) continue;
            if (!_videoPos.TryGetValue(members[0], out var master)) continue; // 基准未上报（暂停/非播放）跳过
            foreach (var mon in members.Skip(1))
            {
                if (!_videoPos.TryGetValue(mon, out var pos)) continue;
                if (Math.Abs(pos - master) > 500)
                {
                    _wallpaperPlayers[mon].Send(new SetVideoPosition { PositionMs = master });
                    Log.Information("视频同步校正：{Mon} 漂移={D:F1}s → 对齐基准", mon, Math.Abs(pos - master) / 1000);
                }
            }
        }
    }

    /// <summary>Governor 暂停所有壁纸播放（IPC）。</summary>
    public void PauseAllWallpapers()
    {
        foreach (var p in _wallpaperPlayers.Values) p.Send(new Pause());
    }

    /// <summary>Governor 恢复所有壁纸播放（IPC）。</summary>
    public void ResumeAllWallpapers()
    {
        foreach (var p in _wallpaperPlayers.Values) p.Send(new Resume());
    }

    /// <summary>M5：显示组只读视图（设置窗口用）。</summary>
    public IReadOnlyList<DisplayGroup> Groups => _displayGroups;

    /// <summary>M5-UI：某屏生效壁纸（组优先 > 独立 > null）。</summary>
    public WallpaperConfig? GetEffectiveWallpaper(string monitorId) => ResolveWallpaper(monitorId).Cfg;

    /// <summary>M5-UI：全局清除所有屏幕的选中态（广播 IPC）。</summary>
    public void ClearAllSelection()
    {
        foreach (var c in _iconChildren.Values) c.Player.Send(new ClearSelection());
    }

    /// <summary>M6 美化：外观变更（设置窗口入口）：广播子进程 + 防抖落盘。</summary>
    public void SetAppearance(int iconSize, string labelStyle)
    {
        _appearance = _appearance with { IconSize = iconSize, LabelStyle = labelStyle };
        foreach (var c in _iconChildren.Values)
            c.Player.Send(new SetAppearance { IconSize = iconSize, LabelStyle = labelStyle });
        Services.LogDb.Audit("settings", "appearance", $"图标 {iconSize}px / 标签 {labelStyle}");
        RequestSave();
    }

    public AppearanceConfig Appearance => _appearance;

    /// <summary>M6 美化：右键菜单配置变更：广播子进程 + 防抖落盘。</summary>
    public void SetMenuConfig(MenuConfig menu)
    {
        _menu = menu;
        foreach (var c in _iconChildren.Values)
            c.Player.Send(new SetMenu
            {
                ShowOpen = menu.ShowOpen, ShowRename = menu.ShowRename,
                ShowDelete = menu.ShowDelete, ShowLocate = menu.ShowLocate,
                ShowSystemMenu = menu.ShowSystemMenu,
                CustomItems = menu.CustomItems.Select(x => new CustomItemDto
                { Name = x.Name, Command = x.Command, Extensions = x.Extensions }).ToList(),
                SystemMenuHidden = menu.SystemMenuHidden.ToList(),
            });
        Services.LogDb.Audit("settings", "menu",
            $"内置(开/关) + 自定义 {menu.CustomItems.Count} 项");
        RequestSave();
    }

    public MenuConfig Menu => _menu;

    /// <summary>B3：开机自启切换（注册表 + config）。</summary>
    public void SetAutoStart(bool enabled)
    {
        DesktopManager.Native.AutoStart.SetEnabled(enabled);
        Services.LogDb.Audit("settings", "autostart", enabled ? "开启" : "关闭");
        RequestSave();
    }

    public bool AutoStartEnabled => DesktopManager.Native.AutoStart.IsEnabled();

    /// <summary>M5：设置窗口 commit：替换显示组 + 全部在线屏重渲染（组优先）+ 防抖落盘。</summary>
    public void SetDisplayGroups(IReadOnlyList<DisplayGroup> groups)
    {
        Services.LogDb.Audit("settings", "groups", $"显示组 {groups.Count} 个");
        _displayGroups = groups.ToList();
        foreach (var mon in _iconChildren.Keys.ToList()) ApplyWallpaperTo(mon);
        RequestSave();
    }

    /// <summary>M5：壁纸解析优先级：有壁纸的组（成员屏）&gt; 独立壁纸 &gt; null。</summary>
    private (WallpaperConfig? Cfg, DisplayGroup? Group) ResolveWallpaper(string monitorId)
        => WallpaperResolver.Resolve(monitorId, _wallpapers, _displayGroups);

    /// <summary>M4（M6 IPC 化）：壁纸配置下发给子进程（无配置 → 空路径隐藏）。</summary>
    private void ApplyWallpaperTo(string monitorId)
    {
        if (!_wallpaperPlayers.TryGetValue(monitorId, out var player)) return;
        var (cfg, group) = ResolveWallpaper(monitorId);

        // M5-T3：组模式虚拟画布（Core.WallpaperResolver 纯函数，单测覆盖）；在线成员 <2 → 降级单屏。
        IntRect? canvas = null;
        IntRect? monRect = null;
        if (group is not null)
        {
            var onlineRects = MonitorEnumerator.Enumerate()
                .Where(m => group.MonitorIds.Contains(m.PersistentId))
                .Select(m => (m.PersistentId, new IntRect(m.X, m.Y, m.X + m.Width, m.Y + m.Height)))
                .ToList();
            var cc = WallpaperResolver.CalcCanvas(monitorId, onlineRects);
            if (cc is { } v) { canvas = v.Canvas; monRect = v.MonRect; }
        }

        Log.Information("壁纸分发(IPC): {Mon} → cfg={Found} path={Path} canvas={Canvas}（独立 {N} 条 + 组 {G} 个）",
            monitorId, cfg is not null, cfg?.Path ?? "(null)", canvas is not null, _wallpapers.Count, _displayGroups.Count);

        player.Send(new SetWallpaper
        {
            Path = cfg?.Path ?? "",
            Kind = cfg is null ? "image" : cfg.Kind switch
            {
                WallpaperKind.Video => "video",
                WallpaperKind.Gif => "gif",
                _ => "image",
            },
            CanvasW = canvas?.Width ?? 0,
            CanvasH = canvas?.Height ?? 0,
            CropX = canvas is not null && monRect is not null ? monRect.Left - canvas.Left : null,
            CropY = canvas is not null && monRect is not null ? monRect.Top - canvas.Top : null,
        });
    }

    // ---------- 拓扑重建 ----------

    /// <summary>M3-T6/M6：拓扑变化重建（热插拔/分辨率/DPI/主屏切换）。
    /// 流程：①现状落盘（子进程布局缓存）→ ②重枚举 → ③关消失屏子进程（布局已在盘上）
    /// → ④重算孤儿/归属 → ⑤存活屏 SetPosition → ⑥新增屏启动子进程 + 恢复布局 → ⑦刷新主屏。</summary>
    public void RebuildToMatchTopology()
    {
        // 1. 现状落盘（含孤儿）。
        AppConfig snapshot;
        try { snapshot = _persistence.SaveAndReturn(); }
        catch (Exception ex)
        {
            Log.Error(ex, "RebuildToMatchTopology：重建前保存失败，放弃重建（保现状）");
            return;
        }

        var monitors = MonitorEnumerator.Enumerate();
        if (monitors.Count == 0) return;

        var online = monitors.Select(m => new MonitorRef(m.PersistentId, m.IsPrimary)).ToList();
        var liveIds = new HashSet<string>(monitors.Select(m => m.PersistentId), StringComparer.Ordinal);

        // 2. 关消失屏的子进程（其布局已含在 snapshot → 插回时按持久 ID 恢复）。
        foreach (var goneId in _iconChildren.Keys.Where(k => !liveIds.Contains(k)).ToList())
        {
            _iconChildren.Remove(goneId, out var iconGone);
            iconGone?.Player.Stop();
            if (_wallpaperPlayers.Remove(goneId, out var wpGone)) wpGone.Stop();
            Log.Information("拓扑重建：显示器离线，停止子进程 {Id}", goneId);
        }

        // 3. 以 snapshot 为新基线重算孤儿 + 每屏归属（Core.TopologyRebuild 纯函数，单测覆盖）。
        var calc = TopologyRebuild.Calculate(snapshot, liveIds);
        _orphanFences.Clear(); _orphanFences.AddRange(calc.OrphanFences);
        _orphanPositions.Clear(); _orphanPositions.AddRange(calc.OrphanPositions);
        _orphanPaths.Clear(); _orphanPaths.UnionWith(calc.OrphanPaths);
        _looseAssignHint = calc.LooseHints;
        var myFencesByMon = calc.FencesByMon;
        var myPositionsByMon = calc.PositionsByMon;

        // 4. 存活屏：主进程直接 Win32 重定位（child 形态下 WPF Left/Top 会双偏移，位置纯 Win32 管）。
        foreach (var m in monitors)
        {
            if (_iconChildren.TryGetValue(m.PersistentId, out var alive))
            {
                DesktopLayerHost.RepositionChild(alive.Player.Hwnd, m.WorkX, m.WorkY, m.WorkWidth, m.WorkHeight);
                alive.Player.Send(new SetPosition { X = m.WorkX, Y = m.WorkY, W = m.WorkWidth, H = m.WorkHeight }); // 通知子进程更新 WPF 尺寸（内容布局用）
            }
            if (_wallpaperPlayers.TryGetValue(m.PersistentId, out var wpAlive))
            {
                DesktopLayerHost.RepositionChild(wpAlive.Hwnd, m.X, m.Y, m.Width, m.Height - 2); // 同启动：2px 缝破全屏检测
                wpAlive.Send(new SetPosition { X = m.X, Y = m.Y, W = m.Width, H = m.Height });
            }
        }

        // 5. 新增屏：启动子进程（插回屏 = Fence/位置/壁纸按 config 原位恢复）+ 补发桌面图标。
        var newMonitors = new List<string>();
        foreach (var m in monitors)
        {
            if (_iconChildren.ContainsKey(m.PersistentId)) continue;
            StartWallpaperPlayer(m);
            StartIconPlayer(m, myFencesByMon[m.PersistentId], myPositionsByMon[m.PersistentId]);
            newMonitors.Add(m.PersistentId);
            Log.Information("拓扑重建：显示器上线，启动子进程 {Id}", m.PersistentId);
        }
        if (newMonitors.Count > 0 && _lastAll.Count > 0)
        {
            foreach (var mon in newMonitors)
                if (_iconChildren.TryGetValue(mon, out var nc))
                    nc.Player.Send(new SetIcons { Items = SplitFor(mon, _lastAll) });
        }

        // 6. 主屏可能切换。
        PrimaryMonitorId = monitors.FirstOrDefault(m => m.IsPrimary)?.PersistentId
                           ?? _iconChildren.Keys.FirstOrDefault() ?? PrimaryMonitorId;

        Log.Information("拓扑重建完成：{Count} 屏子进程，孤儿 Fence={OF} 位置={OP}",
            _iconChildren.Count, _orphanFences.Count, _orphanPositions.Count);
    }

    // ---------- 图标分发（sync → IPC） ----------

    /// <summary>启动全量分发：按归属切分下发各屏（Fence/散落持有 → 该屏；config hint → 该屏；无归属 → 主屏；孤儿跳过）。</summary>
    public void ApplyInitialSnapshot(IReadOnlyList<IconItem> all)
    {
        _lastAll.Clear();
        _lastAll.AddRange(all);
        foreach (var mon in _iconChildren.Keys.ToList())
            _iconChildren[mon].Player.Send(new SetIcons { Items = SplitFor(mon, all) });
    }

    /// <summary>按归属为某屏切分图标全集（与 Distribute 语义一致，供初始/重建/重启补发）。</summary>
    // M6 下沉：路由逻辑在 Core.IconRouter（三级归属：持有→hint→主屏，孤儿跳过；单测覆盖）。
    // hint 字典构建时已只含在线归属（TopologyRebuild），无需再查在线性。
    private List<IconDto> SplitFor(string monitorId, IEnumerable<IconItem> all) =>
        IconRouter.SplitFor(monitorId, all, FindOwnerMonitor, _looseAssignHint, PrimaryMonitorId, _orphanPaths)
            .Select(ToDtoWithPos).ToList();

    /// <summary>增量分发（sync.Changed）。Removed 广播所有子进程（各自 reconcile no-op 防归属竞态）；
    /// Added 路由到归属屏（缓存归属 → hint → 主屏）。孤儿 path 跳过。</summary>
    public void Dispatch(DesktopDiff diff)
    {
        if (diff.Removed.Count > 0)
        {
            var removedPaths = new HashSet<string>(diff.Removed.Select(r => r.FilePath), StringComparer.OrdinalIgnoreCase);
            for (int i = _lastAll.Count - 1; i >= 0; i--)
                if (removedPaths.Contains(_lastAll[i].FilePath)) _lastAll.RemoveAt(i);

            var removedMsg = new ApplyDiff { Removed = diff.Removed.Select(r => r.FilePath).ToList() };
            foreach (var c in _iconChildren.Values) c.Player.Send(removedMsg);
        }

        if (diff.Added.Count == 0) return;
        _lastAll.AddRange(diff.Added);

        var byMonitor = new Dictionary<string, List<IconDto>>(StringComparer.Ordinal);
        foreach (var item in diff.Added)
        {
            if (_orphanPaths.Contains(item.FilePath)) continue;
            var owner = FindOwnerMonitor(item.FilePath);
            if (owner is null && _looseAssignHint.TryGetValue(item.FilePath, out var hint)
                && _iconChildren.ContainsKey(hint)) owner = hint;
            owner ??= PrimaryMonitorId;
            if (owner is null) continue;
            if (!byMonitor.TryGetValue(owner, out var list)) byMonitor[owner] = list = new List<IconDto>();
            list.Add(ToDtoWithPos(item));
        }
        foreach (var (mon, items) in byMonitor)
        {
            if (_iconChildren.TryGetValue(mon, out var c))
                c.Player.Send(new ApplyDiff { Added = items });
        }
    }

    // ---------- 聚合持久化 ----------

    private void RequestSave() => _persistence.RequestSave();

    /// <summary>聚合所有子进程布局缓存 + 孤儿配置（离线屏数据不丢）。</summary>
    private AppConfig BuildAggregatedConfig()
    {
        var fences = new List<FenceConfig>(_orphanFences);
        var positions = new List<IconPosition>(_orphanPositions);
        foreach (var c in _iconChildren.Values)
        {
            fences.AddRange(c.Fences);
            positions.AddRange(c.Positions);
        }
        return new AppConfig
        {
            Fences = fences,
            IconPositions = positions,
            Wallpapers = _wallpapers.ToList(),
            DisplayGroups = _displayGroups.ToList(),
            Appearance = _appearance,
            Menu = _menu,
            AutoStart = DesktopManager.Native.AutoStart.IsEnabled(),
        };
    }

    /// <summary>立即聚合保存（不等防抖）。OnExit 调用；随后 CloseAll。</summary>
    public void SaveAllNow() => _persistence.SaveNow();

    /// <summary>停止所有子进程（OnExit，SaveAllNow 之后）。</summary>
    public void CloseAll()
    {
        foreach (var c in _iconChildren.Values) c.Player.Stop();
        _iconChildren.Clear();
        foreach (var p in _wallpaperPlayers.Values) p.Stop();
        _wallpaperPlayers.Clear();
        _videoSync?.Stop();
    }

    // ---------- DTO 映射 ----------

    private static IconDto ToDto(IconItem i) =>
        new() { Path = i.FilePath, Name = i.DisplayName, X = i.X, Y = i.Y };

    /// <summary>带持久化位置的 DTO（M6 修复：位置下发通道）——config 位置是重启保持的真相源，
    /// 子进程收到 X/Y&gt;0 的项会原位显示（AddLooseIcon 保留坐标），否则网格排位。</summary>
    private IconDto ToDtoWithPos(IconItem i)
    {
        foreach (var c in _iconChildren.Values)
        {
            var hit = c.Positions.FirstOrDefault(p =>
                string.Equals(p.FilePath, i.FilePath, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                return new IconDto { Path = i.FilePath, Name = i.DisplayName, X = hit.X, Y = hit.Y };
        }
        return ToDto(i); // 无记录（新文件）→ X/Y=0 → 子进程网格排位
    }

    internal static FenceDto ToDto(FenceConfig f) => new()
    {
        Id = f.Id, Title = f.Title, X = f.X, Y = f.Y, W = f.W, H = f.H, Collapsed = f.Folded,
        IconPaths = f.IconFilePaths.ToList(),
    };

    internal static FenceConfig FromDto(FenceDto f) => new()
    {
        Id = f.Id, Title = f.Title, X = f.X, Y = f.Y, W = f.W, H = f.H, Folded = f.Collapsed,
        MonitorId = "", // 缓存内归属由字典 key（monitorId）决定；BuildConfig 上报时子进程已打戳，这里作废重置
        IconFilePaths = f.IconPaths.ToList(),
    };
}
