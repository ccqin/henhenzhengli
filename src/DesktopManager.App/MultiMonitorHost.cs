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

    // 孤儿（归属屏离线）：不进任何窗口，保存时原样带回（防布局数据丢失）。
    private readonly List<FenceConfig> _orphanFences = new();
    private readonly List<IconPosition> _orphanPositions = new();
    // 孤儿屏上的全部图标 path（loose + Fence 内容）：初始分发/Added 路由跳过，防误落主屏。
    private readonly HashSet<string> _orphanPaths = new(StringComparer.OrdinalIgnoreCase);

    // 防抖保存（从单窗口时代的 IconLayerWindow 上移）：任一窗口 LayoutChanged → 500ms 后聚合落盘。
    private readonly System.Threading.Timer _saveTimer;
    private readonly object _saveLock = new();
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);
    private volatile bool _savingDisabled;

    /// <summary>主屏窗口（新图标缺省归属 + ShellRestartWatcher 挂载点）。无主屏（畸形拓扑）退化首个窗口。</summary>
    public IconLayerWindow? PrimaryWindow { get; private set; }

    public IReadOnlyCollection<IconLayerWindow> Windows => _windows.Values;

    public MultiMonitorHost(IConfigStore store)
    {
        _store = store;
        _saveTimer = new System.Threading.Timer(_ => OnSaveTimerElapsed(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>枚举显示器 → 加载 config → 按归属切分 → 每屏建窗口并 Show。
    /// 零显示器（理论不发生）抛异常，由 App 走回滚（RestoreExplorer）。</summary>
    public void Attach()
    {
        var monitors = MonitorEnumerator.Enumerate();
        if (monitors.Count == 0)
            throw new InvalidOperationException("未枚举到任何显示器，无法创建图标层");

        var config = _store.Load();
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
            _windows[m.PersistentId] = win;
            win.Show();
            if (win.IsPrimary) PrimaryWindow ??= win;
        }
        PrimaryWindow ??= _windows.Values.First();

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

    /// <summary>启动全量分发：按归属把快照切给各窗口。
    /// 归属判定：Fence 持有（构造期已 LoadIcons）→ 该窗；config 位置记录 → 该窗；都没有 → 主屏（新图标缺省）。</summary>
    public void ApplyInitialSnapshot(IReadOnlyList<IconItem> all)
    {
        var groups = _windows.Keys.ToDictionary(k => k, _ => new List<IconItem>());
        foreach (var item in all)
        {
            if (_orphanPaths.Contains(item.FilePath)) continue; // 孤儿屏图标：不渲染，插回恢复
            var owner = FindOwner(item.FilePath);
            var target = owner?.MonitorId ?? PrimaryWindow!.MonitorId;
            groups[target].Add(item);
        }
        foreach (var (mon, win) in _windows)
            win.ApplySnapshot(groups[mon]);
    }

    /// <summary>增量分发（sync.Changed）。
    /// Removed 广播所有窗口（窗口无该 path 则 reconcile 自然 no-op，避免归属竞态）；
    /// Added 按归属投单窗：Fence/散落持有窗，无归属落主屏。</summary>
    public void Dispatch(DesktopDiff diff)
    {
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
        return new AppConfig { Fences = fences, IconPositions = positions };
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
    }
}
