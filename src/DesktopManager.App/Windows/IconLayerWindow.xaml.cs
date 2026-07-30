using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopManager.App.Converters;
using DesktopManager.App.Controls;
using DesktopManager.App.Services;
using DesktopManager.Core.Models;
using DesktopManager.Core.Services;
using DesktopManager.Native;
using Serilog;

namespace DesktopManager.App.Windows;

public partial class IconLayerWindow : Window, IInteractiveHost
{
    private readonly IconExtractor _icons = new();

    // ---------- M2 真机修复 Bug 2：NOACTIVATE 临时激活（IInteractiveHost） ----------
    // SourceInitialized 时抓取 hwnd 并保留；BeginInput/EndInput 由 FenceControl/RenameDialog 在
    // 文本输入前后调用，保证 app 进程在 NOACTIVATE 设计下仍可获取前台焦点输入。
    private IntPtr _hwnd;
    private long _noActivatePrevEx; // EnableActivation 返回值，EndInput 用其恢复
    private bool _inputActive;      // 防 BeginInput 重入（如多次进入编辑未退出）导致 prevEx 被覆盖

    // ---------- T7：持久化 ----------
    // 配置存储（App.OnStartup 注入）。Save 在 ThreadPool 线程做文件 IO；BuildConfig 在 UI 线程收集。
    private readonly IConfigStore _store;
    // 防抖定时器：变更后 500ms 无新触发才落盘。Change(dueTime, Infinite)：一次性触发，不重复。
    private System.Threading.Timer? _saveTimer;
    // 保护 Save 串行化（防抖回调与 SaveFencesNow 可能竞争同一文件写）。
    private readonly object _saveLock = new();
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);
    private volatile bool _savingDisabled; // OnExit/Dispose 后不再后台写，避免退出竞态。

    // ---------- T3：归属划分 + 最小接线 ----------
    // 当前所有 FenceControl 实例（T7 启动从 ConfigStore 加载，运行期 CreateNewFence/DeleteFence 维护）。
    // P0-T2：FenceControl 是 IconCanvas 的持久子元素（CreateFence 一次 Add），ApplySnapshot/ApplyDiff 不 Clear Children、不重 Add Fence。
    private readonly List<FenceControl> _fences = new();
    // 当前已归属任一 Fence 的 FilePath 集合，ApplySnapshot/ApplyDiff 据此过滤散落区（避免归属图标重复显示）。
    private readonly HashSet<string> _fencedPaths = new(StringComparer.OrdinalIgnoreCase);
    // 当前散落区全集快照（ApplySnapshot 设 / ApplyDiff 增量维护）。
    // T6：OnFenceIconRemoved/DeleteFence 从中按 FilePath 找回 IconItem 单条回填 _looseIcons（保留原 X/Y 位置）。
    private IReadOnlyList<IconItem> _allItems = Array.Empty<IconItem>();

    // ---------- P0-T2：散落图标数据驱动渲染 ----------
    // 散落图标集合（增量驱动 ItemsControl）。P0-T3：ApplySnapshot/ApplyDiff 经 IconSetReconciler 算 toAdd/toRemove 后 mutate。
    // WPF ItemContainerGenerator 在 Add/Remove 时只创建/销毁差异容器 = 免费增量 diff（替代 M2 Children.Clear+重建）。
    private readonly ObservableCollection<IconItem> _looseIcons = new();

    // R2 拖拽中容器回收：_draggedIcon 持 **IconItem 数据引用**（非 UI 容器）。
    // DoDragDrop 期间若 sync 触发 reconcile 回收容器，path 已在 DoDragDrop 调用前 capture 为本地 string，拖拽不受影响。
    private IconItem? _draggedIcon;
    private Point _iconDragOrigin;     // arm 时鼠标位置（窗口坐标），超 MinimumDragDistance 才 DoDragDrop
    private bool _iconDragArmed;       // 单击 arm；双击/松手/超阈值拖出 后清零（三守卫）
    // 右键菜单目标图标（Opening 前 PreviewMouseRightButtonDown hit-test 捕获，Click 复用四项逻辑）。
    private IconItem? _contextMenuIcon;

    // ---------- T4：双击空白切可见性（R4：窗口级 DP，DataTemplate 绑它） ----------
    // 散落图标 DataTemplate 根 StackPanel.Visibility 绑本 DP（RelativeSource AncestorType=Window）；
    // 双击空白切它 → 所有散落图标项显隐由绑定自动同步；FenceControl 仍遍历 IconCanvas.Children 切 Visibility。
    public static readonly DependencyProperty IconVisibilityProperty =
        DependencyProperty.Register(nameof(IconVisibility), typeof(Visibility), typeof(IconLayerWindow),
            new PropertyMetadata(Visibility.Visible));
    public Visibility IconVisibility
    {
        get => (Visibility)GetValue(IconVisibilityProperty);
        set => SetValue(IconVisibilityProperty, value);
    }

    /// <param name="store">T7 注入的配置存储。null 仅用于设计器/极端场景（无持久化）。</param>
    public IconLayerWindow(IConfigStore? store = null)
    {
        // P0-T2：DataTemplate 引用 {StaticResource FilePathToIconConverter}，须在 XAML 解析前注册。
        // 共享同一份 IconExtractor（与所有 FenceControl 共用图标缓存）。
        Resources["FilePathToIconConverter"] = new FilePathToIconConverter(_icons);

        InitializeComponent();
        _store = store ?? new NullConfigStore();
        SourceInitialized += (_, _) =>
        {
            // 铺主屏工作区（不含任务栏），避免遮挡任务栏。M3 多屏改按显示器工作区定位。
            var work = SystemParameters.WorkArea;
            Left = work.Left; Top = work.Top; Width = work.Width; Height = work.Height;
            _hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            WindowInterop.MakeNonInteractiveTopmost(_hwnd); // 不点击穿透，可点图标
        };

        // P0-T2：散落图标集合驱动 LooseItemsControl（XAML 里 DataTemplate/ItemContainerStyle 已就绪）。
        LooseItemsControl.ItemsSource = _looseIcons;
        // 散落图标右键菜单（四项：打开/重命名/删除/打开文件位置）。Opening 前 PreviewMouseRightButtonDown hit-test 捕获 _contextMenuIcon。
        LooseItemsControl.ContextMenu = BuildLooseIconContextMenu();

        // T7：启动防抖定时器（ThreadPool，一次性 due time）。
        _saveTimer = new System.Threading.Timer(_ => OnSaveTimerElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        // T7：从 ConfigStore 加载 Fences（替换 T3 硬编码 f1）。
        // 加载阶段用 LoadIcons（不触发 IconAdded，避免 N*M 次宿主重渲），_fencedPaths 在此直接初始化。
        LoadFencesFromConfig();

        // T6：画布空白右键 → 新建收纳盒。
        IconCanvas.ContextMenu = BuildCanvasContextMenu();
    }

    /// <summary>T7 加载：读 config.Fences → 逐个 CreateFence + LoadIcons + _fencedPaths 初始化。
    /// 容错：IconFilePaths 里已被用户删除的 path 用 IconPathFilter 跳过；空配置不创建任何 Fence。</summary>
    private void LoadFencesFromConfig()
    {
        AppConfig config;
        try { config = _store.Load(); }
        catch (Exception ex)
        {
            // ConfigStore.Load 内部已兜底返回默认；这里再兜一层防 IConfigStore 实现抛异常 → 不阻塞启动。
            Log.Warning(ex, "LoadFencesFromConfig: Load 失败，空配置启动");
            config = new AppConfig();
        }

        foreach (var fc in config.Fences)
        {
            // I3：per-fence try/catch。单个坏 Fence（图标提取失败/Bind 异常/路径非法等）不中断其余加载，
            // 与 ConfigStore.Load 容错理念一致。坏 Fence 跳过，其余照常创建。
            try
            {
                var fence = CreateFence(fc);
                // 容错：跳过已不存在的 path（用户可能在 app 关闭后删除了文件）。
                var existing = IconPathFilter.FilterExisting(fc.IconFilePaths);
                fence.LoadIcons(existing); // 不触发 IconAdded，避免加载阶段重渲风暴
                foreach (var p in existing) _fencedPaths.Add(p);
            }
            catch (System.Exception ex)
            {
                // 单个 Fence 加载失败属可恢复降级（其余 Fence 仍可加载），用 Warning 而非 Error。
                Log.Warning(ex, "LoadFencesFromConfig：Fence {FenceId} 加载失败，跳过该 Fence（其余继续）", fc?.Id);
                continue;
            }
        }
    }

    /// <summary>创建 FenceControl、Bind、加到画布、订阅归属/变更事件、挂右键菜单（重命名/删除）。
    /// T7：注入共享 IconExtractor；订阅 ConfigChanged → 防抖 Save。返回新创建的控件供调用方做加载期补充操作。</summary>
    private FenceControl CreateFence(FenceConfig config)
    {
        var fence = new FenceControl { Icons = _icons }; // T7：跨 Fence 共享图标缓存
        fence.Bind(config);
        Canvas.SetLeft(fence, config.X);
        Canvas.SetTop(fence, config.Y);
        fence.IconAdded += OnFenceIconAdded;
        fence.IconRemoved += OnFenceIconRemoved;
        fence.ConfigChanged += OnFenceConfigChanged; // T7：标题/折叠/拖动 → 防抖 Save
        fence.ContextMenu = BuildFenceContextMenu(fence);
        _fences.Add(fence);
        IconCanvas.Children.Add(fence);
        return fence;
    }

    // ---------- T6：收纳盒右键（新建 / 删除 / 重命名） ----------

    /// <summary>画布空白右键菜单（新建空 Fence）。只挂空白：FenceControl 和散落图标各自有 ContextMenu，不冒泡。</summary>
    private ContextMenu BuildCanvasContextMenu()
    {
        var menu = new ContextMenu();
        var miNew = new MenuItem { Header = "新建收纳盒" };
        miNew.Click += (_, _) => CreateNewFence();
        menu.Items.Add(miNew);
        return menu;
    }

    /// <summary>FenceControl 右键菜单（重命名 / 删除本 Fence）。</summary>
    private ContextMenu BuildFenceContextMenu(FenceControl fence)
    {
        var menu = new ContextMenu();
        var miRename = new MenuItem { Header = "重命名" };
        miRename.Click += (_, _) => fence.BeginRename();
        var miDelete = new MenuItem { Header = "删除收纳盒" };
        miDelete.Click += (_, _) => DeleteFence(fence);
        menu.Items.Add(miRename);
        menu.Items.Add(miDelete);
        return menu;
    }

    /// <summary>新建空 Fence：Id 唯一（Guid 截断），坐标叠加偏移避开现有，加到 _fences + 画布。
    /// T7：触发防抖持久化。</summary>
    private void CreateNewFence()
    {
        var offset = _fences.Count * 30;
        CreateFence(new FenceConfig
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Title = "新收纳盒",
            X = 120 + offset,
            Y = 120 + offset,
            W = 180,
            H = 120
        });
        SaveFencesDebounced(); // T7
    }

    /// <summary>删除本 Fence：确认 → 从 _fences 移除 + 画布移除 + 取消订阅 + 释放归属（_fencedPaths 移除 + 单条回填散落区）。
    /// 关键：该 Fence 的 IconFilePaths 从 _fencedPaths 移除前，先检查无其他 Fence 仍归属（防跨 Fence 单归属误删）。
    /// T6：不调 ApplySnapshot/SetIcons，逐 path 单条 _looseIcons.Add（R5 顺序 + 无闪屏）。</summary>
    private void DeleteFence(FenceControl fence)
    {
        var title = fence.BuildConfig().Title;
        var r = MessageBox.Show(this, $"确定删除收纳盒 \"{title}\"？其内图标将回到散落区。",
            "删除收纳盒", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;

        var paths = fence.BuildConfig().IconFilePaths;
        _fences.Remove(fence);
        IconCanvas.Children.Remove(fence);
        fence.IconAdded -= OnFenceIconAdded;
        fence.IconRemoved -= OnFenceIconRemoved;
        fence.ConfigChanged -= OnFenceConfigChanged; // T7
        // T6：释放归属 + 单条回填（替代 ApplySnapshot 全量兜底）。
        // 该 Fence 的图标若无其他 Fence 仍持有（跨 Fence 单归属防误删），从 _fencedPaths 移除 + 从 _allItems 回填 _looseIcons。
        // 先 _fences.Remove 再检查 _fences.Any(f.ContainsIcon) → fence 自身已排除，只跳过真正仍持有的其他 Fence。
        foreach (var path in paths)
        {
            if (_fences.Any(f => f.ContainsIcon(path))) continue; // 其他 Fence 仍持有，保留归属
            _fencedPaths.Remove(path);
            var it = _allItems.FirstOrDefault(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase));
            // I-1 幽灵图标：fallback 前 File.Exists 守卫——fenced 文件被外部删除后 ApplyDiff 已移除该 path，
            // 此时拖出/删 Fence 触发回填，若不守卫会把磁盘上已不存在的文件加回散落区 → 幽灵图标（sync 不自清）。
            // 文件不存在则 it 保持 null，下方 Add 跳过（不加幽灵、不 NRE）。
            if (it is null && File.Exists(path)) it = new IconItem(path, Path.GetFileName(path));
            if (it is not null && !_looseIcons.Contains(it)) AddLooseIcon(it); // X/Y<=0 网格排位，否则保留原位置；防重复
        }
        SaveFencesDebounced(); // T7
    }

    /// <summary>P0-T3：全量对账渲染（启动/explorer 重启/归属变化兜底用）。
    /// 增量 reconcile：用 <see cref="IconSetReconciler.PlanSnapshot"/> 算 toAdd/toRemove，仅对差异项 mutate <c>_looseIcons</c>
    /// （WPF ItemContainerGenerator 只创建/销毁差异容器 = 无闪屏）。无 <c>IconCanvas.Children.Clear</c>，Fence 不重 Add。
    /// X/Y 来自 IconItem（INPC）；快照无位置（&lt;=0）则按 count 网格排，赋值触发 ItemContainerStyle 的 Canvas.Left/Top 绑定刷新。</summary>
    public void ApplySnapshot(IReadOnlyList<IconItem> all)
    {
        _allItems = all;
        var (toAdd, toRemove) = IconSetReconciler.PlanSnapshot(all, _fencedPaths, _looseIcons);

        // 先 Remove 再 Add：count（网格排位依据）反映删除后的剩余项数，新项顺势补位。
        // toRemove 是 currentLoose（=_looseIcons）原实例引用，Remove 走引用相等命中。
        foreach (var rem in toRemove) _looseIcons.Remove(rem);
        foreach (var add in toAdd) AddLooseIcon(add);
    }

    /// <summary>P0-T3：增量对账渲染（sync.Changed 用）。真正单条增量：Added→Add，Removed→Remove。
    /// <para>_allItems 同步更新（移除 Removed / 追加 toAdd 原项），保证后续 ApplySnapshot 兜底看到最新全集。</para>
    /// <para>复用 diff.Added 原项（DesktopSnapshot 已设 DisplayName=Path.GetFileName、X/Y=0 待网格排），
    /// 同一批实例同时填 _allItems 与 _looseIcons —— review Minor 1：消除双实例。</para>
    /// <para>R9 rename：DesktopDiff 已拆 Removed(旧)+Added(新)，两端各自处理 → 旧名消失、新名出现。</para>
    /// <para>review Important（Fence 死链）：Removed 中属 _fencedPaths 的 path（拖 Fence 图标到 explorer/文件夹
    /// → 文件移走→FSW Deleted），静默清归属 Fence（RemoveIconSilent）+ _fencedPaths 移除 + SaveFencesDebounced，
    /// 避免 Fence 残留死链 + config 持久化死 path。</para></summary>
    public void ApplyDiff(DesktopDiff diff)
    {
        // 1. 增量对账：先算 toAdd/toRemove。PlanDiff 复用 diff.Added 原项引用（review Minor 1：消除双实例）。
        var (toAdd, toRemove) = IconSetReconciler.PlanDiff(diff, _fencedPaths, _looseIcons);

        // 2. 更新 _allItems（全集兜底）：移除 Removed，追加 toAdd 原项。
        //    toAdd 与下面 _looseIcons 追加的同一批 IconItem 为同一实例 → T6 若
        //    _looseIcons.Remove(_allItems.First(...)) 走引用相等命中，不再静默失败。
        var removedPaths = new HashSet<string>(diff.Removed.Select(r => r.FilePath), StringComparer.OrdinalIgnoreCase);
        var rebuilt = new List<IconItem>(_allItems.Count + toAdd.Count);
        foreach (var existing in _allItems)
            if (!removedPaths.Contains(existing.FilePath)) rebuilt.Add(existing);
        foreach (var add in toAdd) rebuilt.Add(add);
        _allItems = rebuilt;

        // 3. review Important：Fence 死链清除。拖 Fence 内容图标到 explorer/文件夹（76d7aff 开通）→
        //    文件被移走 → DesktopSync FSW Deleted → ApplyDiff(diff.Removed 含该 fenced path)。PlanDiff 只按
        //    loosePaths 过滤 Removed（fenced path 不在散落区 → toRemove 不含它）→ Fence _contentIcons/_config
        //    残留死链，且 SaveFencesDebounced 把死 path 持久化进 config（重启仍在，Open 静默失败）。
        //    这里新增：Removed path ∈ _fencedPaths 时，静默清归属 Fence（RemoveIconSilent 不触发 IconRemoved →
        //    不会回填散落区——文件确实已不在磁盘）+ _fencedPaths.Remove + 防抖 Save（config 不持久化死 path）。
        //    单归属不变式：OnFenceIconAdded 已用 RemoveIconSilent 保证一 path 至多属一 Fence；Where 全清是
        //    防御性（旧 config 万一多 Fence 含同 path，一次性清干净，与 OnFenceIconAdded 对称）。顺序在
        //    _looseIcons mutate 前：PlanDiff 已过滤 fenced，下面 _looseIcons 逻辑不会重复处理这些 path。
        bool fenceChanged = false;
        foreach (var path in removedPaths)
        {
            if (!_fencedPaths.Contains(path)) continue; // 散落区 path：交给下面 _looseIcons 逻辑
            foreach (var f in _fences.Where(f => f.ContainsIcon(path)))
                f.RemoveIconSilent(path);
            _fencedPaths.Remove(path);
            fenceChanged = true;
        }
        if (fenceChanged) SaveFencesDebounced();

        // 4. 增量 mutate _looseIcons（UI 线程，R7）。
        //    倒序 RemoveAt：按 path 匹配删除，避免索引前移错位。
        var removeSet = new HashSet<string>(toRemove, StringComparer.OrdinalIgnoreCase);
        for (int i = _looseIcons.Count - 1; i >= 0; i--)
        {
            if (removeSet.Contains(_looseIcons[i].FilePath))
                _looseIcons.RemoveAt(i);
        }
        foreach (var add in toAdd) AddLooseIcon(add);
    }

    /// <summary>网格排位 + Add：X/Y 均 &lt;=0（需自动排位）时找**空闲 slot**（不与现有 _looseIcons 重叠）；
    /// 已定位项（X/Y&gt;0，如拖出回填保留原位）直接用原 X/Y。赋值触发 INPC → ItemContainerStyle 的
    /// Canvas.Left/Top 绑定刷新（T1 INPC 收益）。网格：10 列宽 90 / 行高 96，原点 (16,16)（M1 排版）。
    /// <para>真机修复（拖进拖出后重叠）：原先按 count 算 slot，拖入拖出后 _looseIcons 数量变化，count 算出的
    /// slot 可能与保留原 X/Y 的现有图标撞上。改遍历网格找第一个空闲 slot（与现有图标 X/Y 均差 &lt;1 视为占用）。</para></summary>
    private void AddLooseIcon(IconItem item)
    {
        // 仅当 X/Y 均 <=0（需自动排位）时找空闲 slot；半定位（一轴 >0 一轴 <=0）实际不出现（IconItem 默认 0/0，
        // 回填保留双轴 >0），保守用“均 <=0”门控与原语义对齐。已定位项保留原 X/Y（如拖出回填、rename 项）。
        if (item.X <= 0 && item.Y <= 0)
        {
            (item.X, item.Y) = FindFreeLooseSlot();
        }
        _looseIcons.Add(item);
    }

    /// <summary>遍历 10 列网格（col=0..9, row=0..递增）找第一个不与现有 _looseIcons 重叠的 slot。
    /// 重叠判定：与某现有图标 X 差 &lt;1 且 Y 差 &lt;1（同 slot）。槽位无限、图标有限 → 必终止。</summary>
    private (double x, double y) FindFreeLooseSlot()
    {
        const double originX = 16, originY = 16;
        const double stepX = 90, stepY = 96;
        const int cols = 10;
        for (int row = 0; ; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                double x = originX + col * stepX;
                double y = originY + row * stepY;
                bool occupied = false;
                foreach (var existing in _looseIcons)
                {
                    if (Math.Abs(existing.X - x) < 1 && Math.Abs(existing.Y - y) < 1)
                    {
                        occupied = true;
                        break;
                    }
                }
                if (!occupied) return (x, y);
            }
        }
    }

    // ---------- T3：Fence 归属事件回调 ----------

    private void OnFenceIconAdded(FenceControl fence, string filePath)
    {
        // R5 顺序：_fencedPaths 先占位。防并发 sync.Changed→ApplyDiff/ApplySnapshot 把新归属图标误当散落补回
        //（T6 去了归属兜底，但 sync 增量路径仍读 _fencedPaths，顺序必须保持）。
        _fencedPaths.Add(filePath);
        // review-finding 3：跨 Fence 迁移防双重归属。拖入本 fence 后，若 path 仍属其他 Fence（如从 A 拖到 B），
        // 把它从那些 Fence **静默**移除（RemoveIconSilent 不触发 IconRemoved）。
        // 关键时序：若用普通 RemoveIcon 会触发 OnFenceIconRemoved → _fencedPaths.Remove(filePath)，
        // 但此时新 owner fence 仍拥有 filePath，_fencedPaths 必须保留它 → 会错误丢失归属。
        // 静默移除只清原 owner 的 _contentIcons/_config/UI，不影响 _fencedPaths，保证 path 最终单归属且 _fencedPaths 含 path。
        foreach (var other in _fences.Where(f => f != fence && f.ContainsIcon(filePath)))
        {
            other.RemoveIconSilent(filePath);
        }
        // T6：单条增删（替代 ApplySnapshot 全量兜底）。OnFenceIconAdded 由 Fence_Drop→AddIcon→IconAdded 触发，
        // 本身在 UI 线程，无需 Dispatcher.BeginInvoke（R7）。FilePath 匹配（T3 单实例下引用也命中，FilePath 更稳）。
        var loose = _looseIcons.FirstOrDefault(i => string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (loose is not null) _looseIcons.Remove(loose);
        SaveFencesDebounced(); // T7：归属变化持久化
    }

    private void OnFenceIconRemoved(FenceControl fence, string filePath)
    {
        _fencedPaths.Remove(filePath);
        // T6：单条回填（替代 ApplySnapshot 全量兜底）。从 _allItems 找回 IconItem（T3 单实例 → 保留拖入前的原 X/Y 位置）；
        // 找不到（罕见：新文件被直接 fenced 且从未进散落区）则构造新项，AddLooseIcon 统一网格排位。
        var it = _allItems.FirstOrDefault(i => string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        // I-1 幽灵图标：fallback 前 File.Exists 守卫——fenced 文件被外部删除后 ApplyDiff 已移除该 path，
        // 此时拖出/删 Fence 触发回填，若不守卫会把磁盘上已不存在的文件加回散落区 → 幽灵图标（sync 不自清）。
        // 文件不存在则 it 保持 null，下方 Add 跳过（不加幽灵、不 NRE）。
        if (it is null && File.Exists(filePath)) it = new IconItem(filePath, Path.GetFileName(filePath));
        // 防重复事件：it 已在散落区则不重复 Add（Contains 走引用相等，T3 单实例下可靠）。
        if (it is not null && !_looseIcons.Contains(it)) AddLooseIcon(it); // X/Y<=0 时网格排位，否则保留原位置
        SaveFencesDebounced(); // T7
    }

    /// <summary>T7：FenceControl 标题/折叠/拖动坐标变化 → 防抖持久化。</summary>
    private void OnFenceConfigChanged()
    {
        SaveFencesDebounced();
    }

    // ---------- T7：防抖持久化 ----------

    /// <summary>防抖触发：500ms 内无新变更才落盘。每次调用重置计时器。
    /// 触发点：①归属变化（OnFenceIconAdded/Removed）；②新建/删除 Fence；③FenceControl.ConfigChanged。</summary>
    private void SaveFencesDebounced()
    {
        if (_savingDisabled) return;
        var t = _saveTimer;
        if (t is null) return;
        // Change(dueTime, period)：dueTime=500ms 后触发一次，period=Infinite 不重复。每次调用重置 dueTime → 防抖。
        lock (_saveLock)
        {
            t.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Timer 回调（ThreadPool 线程）：收集 BuildConfig（须 UI 线程）→ Save（文件 IO，任意线程）。
    /// 线程安全：BuildConfig 经 Dispatcher.Invoke 在 UI 线程读 Canvas 附加属性；Save 经 _saveLock 串行化。</summary>
    private void OnSaveTimerElapsed()
    {
        if (_savingDisabled) return;
        try
        {
            // Dispatcher.Invoke 同步回 UI 线程收集（BuildConfig 读 Canvas.GetLeft/Top + ActualWidth/Height）。
            // 收集在 UI 线程做（快，无文件 IO）；结果带回 ThreadPool 线程做文件写。
            AppConfig appConfig;
            if (Dispatcher.CheckAccess())
                appConfig = BuildAppConfigForSave();
            else
                appConfig = (AppConfig)Dispatcher.Invoke(new Func<AppConfig>(BuildAppConfigForSave));

            lock (_saveLock)
            {
                if (_savingDisabled) return;
                _store.Save(appConfig); // ConfigStore: 原子 tmp + File.Replace，线程安全
            }
        }
        catch (Exception ex)
        {
            // 持久化失败不应崩 UI（同 ConfigStore 异常兜底理念）。下次变更会再触发重试。
            Log.Warning(ex, "防抖保存失败");
        }
    }

    /// <summary>收集当前 _fences 状态为 AppConfig（纯 UI 线程逻辑，供防抖回调和 OnExit 共用）。</summary>
    private AppConfig BuildAppConfigForSave()
    {
        var fences = _fences.Select(f => f.BuildConfig()).ToList();
        return new AppConfig { Fences = fences };
    }

    /// <summary>立即保存（不等防抖）。OnExit 调用，确保退出时布局落盘。
    /// 必须在 UI 线程调（OnExit 是 UI 线程）。先停掉待触发的防抖定时器，再同步收集 + Save。
    /// 与待执行的防抖回调通过 _saveLock + _savingDisabled 串行/短路，避免退出竞态丢最后一次写。</summary>
    public void SaveFencesNow()
    {
        // 停掉待触发的防抖定时器（已 in-flight 的回调由 _savingDisabled + lock 兜底）。
        _savingDisabled = true;
        try { _saveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        catch { /* 释放中可能抛 ObjectDisposedException，忽略 */ }

        try
        {
            var appConfig = BuildAppConfigForSave(); // 已在 UI 线程
            lock (_saveLock)
            {
                _store.Save(appConfig);
            }
        }
        catch (Exception ex)
        {
            // 退出保存失败不阻塞 RestoreExplorer/ClearSelfCleanup（OnExit 兜底）。
            Log.Error(ex, "退出保存失败");
        }
    }

    /// <summary>设计器/极端场景（未注入 store）的 no-op 实现，避免 null 检查散落。</summary>
    private sealed class NullConfigStore : IConfigStore
    {
        public AppConfig Load() => new AppConfig();
        public void Save(AppConfig config) { /* no-op */ }
    }

    // ---------- M2 真机修复 Bug 2：IInteractiveHost 实现 ----------
    // IconLayerWindow 全屏 NOACTIVATE：不抢 explorer 焦点（M1 设计）。但导致 app 内 TextBox 都打不出字。
    // BeginInput 临时去 NOACTIVATE + 前台化；EndInput 恢复 NOACTIVATE + SendToBottom 回桌面层。
    // 严格成对：FenceControl.BeginTitleEdit/EndTitleEdit、RenameDialog.AskRename 的 try/finally 保证。

    /// <summary>临时去 WS_EX_NOACTIVATE 并前台化，让 app 内 TextBox 可接收键盘输入。</summary>
    public void BeginInput()
    {
        if (_inputActive) return; // 防重入：已在输入态时不再覆盖 _noActivatePrevEx（避免丢失原快照）
        if (_hwnd == IntPtr.Zero) return; // SourceInitialized 未跑（极端早调用）：静默放弃，TextBox 仍可能可输入
        _noActivatePrevEx = WindowInterop.EnableActivation(_hwnd);
        _inputActive = true;
    }

    /// <summary>恢复 WS_EX_NOACTIVATE 并 SendToBottom 回桌面层 Z-order。</summary>
    public void EndInput()
    {
        if (!_inputActive) return; // 与 BeginInput 未配对（如 BeginInput 早返回）：no-op 防误恢复
        if (_hwnd != IntPtr.Zero)
        {
            WindowInterop.RestoreNonInteractive(_hwnd, _noActivatePrevEx);
        }
        _inputActive = false;
    }

    // ---------- T3：画布空白 Drop（从 Fence 内容区拖出到空白） ----------

    private void IconCanvas_DragOver(object sender, DragEventArgs e)
    {
        // FileDrop（外部文件从 explorer/文件夹拖入）或 Text（app 图标拖出空白）都接受 Move；其他 None。
        // M2 真机修复前仅认 Text，导致外部文件拖到桌面无反馈、Drop 不处理 → 文件进不了桌面。
        e.Effects = (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void IconCanvas_Drop(object sender, DragEventArgs e)
    {
        // 优先级：Text present → app 图标拖出空白（app 图标 DataObject 含 Text+FileDrop，但语义是
        //   "从 Fence 移除归属回散落区"，文件本身已在桌面不应移动 → 走 Text 分支，不触发 FileDrop 移动）。
        // 否则 FileDrop present → 外部文件移到桌面（explorer/文件夹拖入，仅 FileDrop 无 Text）。
        // 两者都 present 必为 app 图标（见 Loose_PreviewMouseMove / FenceControl MouseMove 构造的 DataObject）。
        if (e.Data.GetDataPresent(DataFormats.Text))
        {
            var path = (string)e.Data.GetData(DataFormats.Text);
            // 定位该 FilePath 的归属 Fence；从其移除（触发 IconRemoved → 异步重渲散落区，图标自动回填）。
            // 若 path 不属于任何 Fence（散落图标拖到空白），无 owner，什么都不做。
            var owner = _fences.FirstOrDefault(f => f.ContainsIcon(path));
            owner?.RemoveIcon(path);
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (files is not null && files.Length > 0) MoveExternalFilesToDesktop(files);
            e.Handled = true;
            return;
        }

        e.Handled = true; // 其他格式：吞掉，避免冒泡到无处理者
    }

    /// <summary>把外部文件（非桌面）移到用户桌面。已在桌面（用户/公共）跳过；
    /// 同名冲突递增 (2)…（不覆盖，与 explorer 命名一致）。跨卷 Move 失败降级 Copy+Delete。
    /// 成功后 DesktopSync 的 FSW 自动检测 Created → Changed → ApplyDiff 增量渲染，无需手动刷新 IconLayer。</summary>
    private void MoveExternalFilesToDesktop(string[] files)
    {
        var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        // existing = 用户+公共桌面当前文件（判断"已在桌面" + 同名冲突基线）。公共桌面只读，仅作判断不写入。
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        })
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir)) existing.Add(f);
        }

        var plans = DesktopDropPlanner.Plan(files, desktopDir, existing);
        foreach (var p in plans)
        {
            if (p.Target is null) continue; // 已在桌面，跳过
            // Minor 2：跳过目录（File.Move 对目录抛异常；M2 范围仅文件，目录拖入 YAGNI）。
            // Plan 是纯函数不碰 FS（单测隔离），目录守卫留在调用方，保持 Plan 决策可单测。
            if (Directory.Exists(p.Source)) continue;
            try
            {
                // 跨卷 File.Move 抛 IOException → 降级 Copy + Delete（与 explorer 跨卷默认"复制"语义对齐，
                // 但本工具按 DragDropEffects.Move 语义最终删除源，符合用户从文件夹"移到桌面"的预期）。
                try { File.Move(p.Source, p.Target); }
                catch (IOException) { File.Copy(p.Source, p.Target); File.Delete(p.Source); }
            }
            catch (Exception ex)
            {
                // 单文件失败不中断其余（权限/占用/源只读等）。FSW 后续对账兜底；错误可见于日志便于诊断。
                Log.Warning(ex, "IconCanvas_Drop：移动外部文件 {Source} -> {Target} 失败", p.Source, p.Target);
            }
        }
    }

    // ---------- T4：双击画布空白切可见性 ----------

    private void IconCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        // hit-test：只有命中画布空白本身（OriginalSource 是 IconCanvas）才触发隐藏/显示。
        // R1 穿透：散落图标的 ItemsPanel Background=null，空白不参与 hit-test → 点击穿透回 IconCanvas（OriginalSource==IconCanvas）；
        // 点散落图标（StackPanel 内 Image/TextBlock，根 StackPanel Background=Transparent 命中）或 FenceControl 子元素时
        // OriginalSource 是这些子元素而非 Canvas，不触发本逻辑（交给散落 Preview 双击 Open / 盒子交互）。
        if (!ReferenceEquals(e.OriginalSource, IconCanvas)) return;
        // R4：切窗口级 IconVisibility DP。散落图标 DataTemplate.Visibility 绑它 → 所有散落项自动同步显隐。
        IconVisibility = IconVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        var vis = IconVisibility;
        // FenceControl 不在 DataTemplate 里，无法靠绑定；遍历 IconCanvas.Children 切 FenceControl.Visibility。
        // LooseItemsControl 本身无需切（其内散落项已由绑定各自 Collapsed；空 ItemsControl 视觉为空，不影响）。
        foreach (UIElement child in IconCanvas.Children)
        {
            if (child is FenceControl fence)
                fence.Visibility = vis;
        }
        e.Handled = true;
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* M1 真机验收记录失败 case */ }
    }

    // ---------- P0-T2：散落图标拖拽（R2/R3 三守卫，Layouter 数据引用模式）+ 右键 ----------

    /// <summary>沿可视树向上找 DataContext 为 <see cref="IconItem"/> 的元素（DataTemplate 内 ContentPresenter 及其子元素均继承该 DataContext）。</summary>
    private static IconItem? FindIconFromSource(object? source)
    {
        var el = source as DependencyObject;
        while (el is not null)
        {
            if (el is FrameworkElement fe && fe.DataContext is IconItem icon) return icon;
            el = VisualTreeHelper.GetParent(el);
        }
        return null;
    }

    /// <summary>隧道 PreviewDown：双击 Open（清 armed 不 arm）；单击 arm（持数据引用）。三守卫之一。</summary>
    private void Loose_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var icon = FindIconFromSource(e.OriginalSource);
        if (icon is null) return;
        // review-finding 1：双击=两次 down，第一次(ClickCount=1)已 arm。第二次 down 必须清 armed，
        // 否则双击后保持按住+移动 → MouseMove 满足 armed+Pressed+超阈值 → 误触 DoDragDrop。
        if (e.ClickCount >= 2)
        {
            _iconDragArmed = false;
            _draggedIcon = null;
            Open(icon.FilePath);
            return;
        }
        _iconDragArmed = true;
        _draggedIcon = icon; // R2：IconItem 数据引用，非 UI 容器
        _iconDragOrigin = e.GetPosition(this);
    }

    /// <summary>review-finding 2：单击松手未移动 → 清 armed（防 armed 残留到下次 down 叠加误触）。三守卫之二。</summary>
    private void Loose_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _iconDragArmed = false;
        _draggedIcon = null;
    }

    /// <summary>Move 超 MinimumDragDistance 才 DoDragDrop（三守卫之三）。
    /// R2：path 在 DoDragDrop 前 capture 为本地 string，DoDragDrop 期间容器回收（sync 触发 reconcile）不影响拖拽数据。</summary>
    private void Loose_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_iconDragArmed || _draggedIcon is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _iconDragOrigin.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(pos.Y - _iconDragOrigin.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            var path = _draggedIcon.FilePath; // capture（拖拽期间 _draggedIcon 可能被清）
            _iconDragArmed = false;
            _draggedIcon = null;
            // M2 真机修复：DataObject 同时含 FileDrop（explorer/文件夹认这个 + 系统拖拽视觉反馈）
            // + Text（兼容 Fence_Drop / IconCanvas_Drop 按 Text 读 app 图标归属）。
            // 单纯 Text 格式 explorer 不认 → 拖到文件夹无反应且无拖拽图标反馈。
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { path });
            data.SetData(DataFormats.Text, path);
            // 拖源用 LooseItemsControl（根 ItemsControl，稳定不回收）。
            DragDrop.DoDragDrop(LooseItemsControl, data, DragDropEffects.Move);
        }
    }

    /// <summary>右键按下时 hit-test 捕获目标图标（供 ContextMenu 四项 Click 复用）。不设 Handled → ContextMenu 正常打开。</summary>
    private void Loose_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _contextMenuIcon = FindIconFromSource(e.OriginalSource);
    }

    /// <summary>构造散落图标右键菜单（四项）。挂 LooseItemsControl.ContextMenu；Opening 前 PreviewMouseRightButtonDown 已捕 _contextMenuIcon。</summary>
    private ContextMenu BuildLooseIconContextMenu()
    {
        var menu = new ContextMenu();
        var miOpen = new MenuItem { Header = "打开" };
        miOpen.Click += (_, _) => { if (_contextMenuIcon is not null) Open(_contextMenuIcon.FilePath); };
        var miRename = new MenuItem { Header = "重命名" };
        miRename.Click += (_, _) => { if (_contextMenuIcon is not null) RenameIcon(_contextMenuIcon.FilePath); };
        var miDelete = new MenuItem { Header = "删除" };
        miDelete.Click += (_, _) => { if (_contextMenuIcon is not null) DeleteIcon(_contextMenuIcon.FilePath); };
        var miLocate = new MenuItem { Header = "打开文件位置" };
        miLocate.Click += (_, _) => { if (_contextMenuIcon is not null) OpenFileLocation(_contextMenuIcon.FilePath); };
        menu.Items.Add(miOpen);
        menu.Items.Add(miRename);
        menu.Items.Add(miDelete);
        menu.Items.Add(miLocate);
        return menu;
    }

    /// <summary>重命名：自定义对话框（预填完整文件名）→ ResolveRenamePath 校验 → File.Move。
    /// 成功后 DesktopSync 的 FSW 自动检测 Renamed → Changed → ApplyDiff 增量重渲，UI 自动同步（旧名消失、新名出现）。</summary>
    private void RenameIcon(string oldPath)
    {
        try
        {
            var oldName = Path.GetFileName(oldPath);
            var newName = RenameDialog.AskRename(this, "重命名", oldName);
            if (newName is null) return; // 用户取消

            var result = IconFileOps.ResolveRenamePath(oldPath, newName);
            if (!result.Ok)
            {
                MessageBox.Show(this, result.Error, "重命名", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            File.Move(oldPath, result.NewPath!);
            // 不手动刷新 IconLayer：DesktopSync.Changed 已在 App.xaml.cs 接到 ApplyDiff，FSW 会触发增量重渲。
        }
        catch (Exception ex)
        {
            // 错误兜底：文件占用 / 权限丢失等不致崩，给用户可见提示。
            MessageBox.Show(this, $"重命名失败：{ex.Message}", "重命名", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>删除：MessageBox 确认 → 走回收站（不永久删，Controller 决议）。
    /// Microsoft.VisualBasic.FileIO 在 net10.0-windows 框架内可用。成功后 DesktopSync 自动检测 Deleted → 重渲。</summary>
    private void DeleteIcon(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            var r = MessageBox.Show(this, $"确定把 \"{name}\" 移到回收站？", "删除确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;

            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            // 不手动刷新：DesktopSync.Changed → ApplyDiff 自动移除该图标。
        }
        catch (Exception ex)
        {
            // 兜底：回收站不可用 / 文件占用（VisualBasic UIOption.OnlyErrorDialogs 已自带系统级错误框，这里是托管层兜底）。
            MessageBox.Show(this, $"删除失败：{ex.Message}", "删除", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>打开文件位置：资源管理器打开并选中该文件（explorer.exe /select）。</summary>
    private static void OpenFileLocation(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* 资源管理器启动失败，静默兜底（与 Open 一致） */ }
    }
}
