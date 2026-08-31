using System.Windows.Threading;
using System.Windows.Interop;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopManager.Player.Icons;
using DesktopManager.Core.Models;
using DesktopManager.Core.Services;
using DIpc = DesktopManager.Ipc;
using DesktopManager.Native;
using Serilog;

namespace DesktopManager.Player.Icons;

/// <summary>M6：跨屏操作桥接口——子进程内通过 IPC 请主进程中转（跨屏拖拽迁移 / 全局清选中）。</summary>
public interface ICrossScreenHost
{
    /// <summary>图标跨屏迁移：path 属其他屏（本窗查无）→ 主进程找源屏导出后导入本屏 pos 处。</summary>
    void TransferLoose(string path, Point pos);

    /// <summary>Fence 跨屏迁移（同窗分支由本窗 MoveFence 处理，不走这里）。</summary>
    void TransferFence(string fenceId, Point pos);

    /// <summary>清除所有屏选中态（本屏稍后自行重选，或本屏主动清除）。</summary>
    void ClearAllSelection();
}

public partial class IconLayerWindow : Window, IInteractiveHost
{
    // Win32 常量和 P/Invoke
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    

    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_LAYERED = 0x00080000;

    private const int WM_SHOWWINDOW = 0x0018;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int SWP_FRAMECHANGED = 0x0020;

    
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly IconExtractor _icons = new();

    // ---------- M2 真机修复 Bug 2：NOACTIVATE 临时激活（IInteractiveHost） ----------
    // SourceInitialized 时抓取 hwnd 并保留；BeginInput/EndInput 由 FenceControl/RenameDialog 在
    // 文本输入前后调用，保证 app 进程在 NOACTIVATE 设计下仍可获取前台焦点输入。
    private IntPtr _hwnd;
    private long _noActivatePrevEx; // EnableActivation 返回值，EndInput 用其恢复
    private bool _inputActive;

    // ---------- M3-T3/T4：多屏化 ----------
    // 窗口与显示器 1:1：构造接收显示器信息（定位用工作区 + 持久 ID）与本屏 Fence/位置子集（host 按归属切分）。
    // 持久化上移 MultiMonitorHost：布局变更只发 LayoutChanged，host 聚合所有窗口 + 孤儿配置后防抖落盘。
    private readonly string _monitorId;
    private readonly (int X, int Y, int W, int H) _workArea;

    /// <summary>本窗口归属屏持久 ID（BuildLayout 打戳 + host 分发路由用）。</summary>
    public string MonitorId => _monitorId;
    public bool IsPrimary { get; }

    /// <summary>布局变更（Fence/散落图标增删拖/折叠/标题等）→ host 防抖聚合保存。</summary>
    public event Action? LayoutChanged;


    /// <summary>M6：跨屏协调桥（IPC 到主进程中转），Attach 后注入。</summary>
    public ICrossScreenHost? Host { get; set; }


    /// <summary>M3-T6：分辨率/排列变化后重定位到新工作区（图标本地坐标不换算——工作区变小时超界项容忍，backlog）。</summary>
    public void RepositionTo(MonitorInfo monitor)
    {
        Left = monitor.WorkX; Top = monitor.WorkY;
        Width = monitor.WorkWidth; Height = monitor.WorkHeight;
    }

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

    // 自由摆放：散落图标持久化位置缓存（仅启动加载用，运行期不更新——BuildAppConfigForSave 直接读 _looseIcons）。
    // AddLooseIcon 在 X/Y<=0 时优先查此缓存，命中用存的 X/Y（重启保持），否则 FindFreeLooseSlot 网格排位。
    // key=FilePath（OrdIgnoreCase）；OrdIgnoreCase 与 _fencedPaths 一致，避免大小写差异导致 cache miss。
    private readonly Dictionary<string, (double X, double Y)> _iconPositions = new(StringComparer.OrdinalIgnoreCase);

    // R2 拖拽中容器回收：_draggedIcon 持 **IconItem 数据引用**（非 UI 容器）。
    // DoDragDrop 期间若 sync 触发 reconcile 回收容器，path 已在 DoDragDrop 调用前 capture 为本地 string，拖拽不受影响。
    private IconItem? _draggedIcon;
    private Point _iconDragOrigin;     // arm 时鼠标位置（窗口坐标），超 MinimumDragDistance 才 DoDragDrop
    private bool _iconDragArmed;       // 单击 arm；双击/松手/超阈值拖出 后清零（三守卫）
    // 右键菜单目标图标（Opening 前 PreviewMouseRightButtonDown hit-test 捕获，Click 复用四项逻辑）。
    private IconItem? _contextMenuIcon;

    // 自由摆放：IconCanvas_Drop 记录 Drop 位置（LooseItemsControl 坐标系，与 IconItem.X/Y 一致），
    // 供 OnFenceIconRemoved 回填（Fence 图标拖出空白 → 落到 Drop 位置，而非网格/原位）。
    // 一次性：OnFenceIconRemoved 读后立即清 null（防下次非 Drop 触发的 RemoveIcon 误用残留值）。
    private Point? _dropPosition;

    // ---------- M6 美化：外观 DP（主进程 SetAppearance IPC 下发） ----------
    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(int), typeof(IconLayerWindow),
            new PropertyMetadata(48, (d, _) => ((IconLayerWindow)d).OnAppearanceChanged()));
    /// <summary>图标尺寸档：32/48/64（绑定模板 Image 与 FenceControl 图标）。</summary>
    public int IconSize
    {
        get => (int)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public static readonly DependencyProperty LabelStyleProperty =
        DependencyProperty.Register(nameof(LabelStyle), typeof(string), typeof(IconLayerWindow),
            new PropertyMetadata("shadow"));
    /// <summary>文字标签风格：shadow（原生阴影，默认）/ pill（现代胶囊）。</summary>
    public string LabelStyle
    {
        get => (string)GetValue(LabelStyleProperty);
        set => SetValue(LabelStyleProperty, value);
    }

    /// <summary>标签最大宽度（跟随 IconSize，XAML 无法做属性算术，靠 INPC 通知）。</summary>
    public double LabelWidth => IconSize + 32;

    private void OnAppearanceChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LabelWidth)));
        _icons.Size = IconSize; // 提取尺寸档同步（缓存 key 含尺寸）
    }

    // INPC（供 LabelWidth 绑定刷新）
    public event PropertyChangedEventHandler? PropertyChanged;

    // ---------- M6 美化：可配置右键菜单 ----------
    // 菜单配置（主进程 SetMenu 下发）；右键时动态构建（配置变更即时生效）。
    internal MenuCfg Menu { get; private set; } = new();

    /// <summary>诊断：菜单配置到达子进程（计数）。</summary>
    internal static event Action<int>? MenuApplied;

    internal sealed class MenuCfg
    {
        public bool ShowOpen = true, ShowRename = true, ShowDelete = true, ShowLocate = true, ShowSystemMenu = true;
        public List<(string Name, string Command, string Extensions)> Custom = new();
        public List<string> SystemHidden = new();
    }

    internal void ApplyMenu(DIpc.SetMenu m)
    {
        MenuApplied?.Invoke(m.CustomItems.Count);  // 诊断（静态事件）：主进程日志可见子进程确实收到
        Menu = new MenuCfg
        {
            ShowOpen = m.ShowOpen, ShowRename = m.ShowRename, ShowDelete = m.ShowDelete,
            ShowLocate = m.ShowLocate, ShowSystemMenu = m.ShowSystemMenu,
            Custom = m.CustomItems.Select(c => (c.Name, c.Command, c.Extensions)).ToList(),
            SystemHidden = m.SystemMenuHidden.ToList(),
        };
    }

    /// <summary>按当前配置构建图标右键菜单项（散落/盒内共用；右键时调用，配置即时生效）。</summary>
    internal void FillIconMenu(ContextMenu menu, string path)
    {
        menu.Items.Clear();
        if (Menu.ShowOpen)
        {
            var mi = new MenuItem { Header = "打开" };
            mi.Click += (_, _) => Open(path);
            menu.Items.Add(mi);
        }
        bool isShell = path.StartsWith("::", StringComparison.Ordinal); // shell 虚拟对象（此电脑/回收站）
        if (Menu.ShowRename && !isShell)
        {
            var mi = new MenuItem { Header = "重命名" };
            mi.Click += (_, _) => RenameIcon(path);
            menu.Items.Add(mi);
        }
        if (Menu.ShowDelete && !isShell)
        {
            var selection = SelectedIcons;
            bool batch = selection.Count >= 2 && selection.Any(i => i.FilePath == path);
            var mi = new MenuItem { Header = batch ? $"删除（{selection.Count} 个项目）" : "删除" };
            mi.Click += (_, _) =>
            {
                if (batch) BatchDelete(selection.Select(i => i.FilePath).ToList());
                else DeleteIcon(path);
            };
            menu.Items.Add(mi);
        }
        if (Menu.ShowLocate && !isShell)
        {
            var mi = new MenuItem { Header = "打开文件位置" };
            mi.Click += (_, _) => OpenFileLocation(path);
            menu.Items.Add(mi);
        }

        // 自定义项（扩展名过滤：空=全部）
        var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var customs = Menu.Custom.Where(c =>
            string.IsNullOrWhiteSpace(c.Extensions) ||
            c.Extensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.ToLowerInvariant()).Contains(ext)).ToList();
        if (customs.Count > 0)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            foreach (var (name, command, _) in customs)
            {
                var mi = new MenuItem { Header = name };
                mi.Click += (_, _) => RunCustomCommand(command, path);
                menu.Items.Add(mi);
            }
        }

        if (Menu.ShowSystemMenu)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            var mi = new MenuItem { Header = "更多操作 ▸" };
            mi.Click += (_, _) =>
            {
                // 事件 handler 异常会崩进程（真机踩坑：InvokeCommand 失败的异常冒泡 = 子进程退出重启）
                try { DesktopManager.Native.SystemContextMenu.Show(_hwnd, path, Menu.SystemHidden); }
                catch (Exception ex) { OpenReported?.Invoke(path, $"系统菜单执行失败: {ex.Message}"); }
            };
            menu.Items.Add(mi);
        }
    }

    /// <summary>自定义命令：{path}/{dir} 占位符替换后经 cmd 执行（隐藏窗口）。</summary>
    private static void RunCustomCommand(string template, string filePath)
    {
        try
        {
            var expanded = template
                .Replace("{path}", filePath)
                .Replace("{dir}", System.IO.Path.GetDirectoryName(filePath) ?? "")
                .Replace('“', '"').Replace('”', '"')  // 全角引号归一化（中文输入法常见）
                .Replace('‘', '\'').Replace('’', '\'');
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c {expanded}")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(filePath) ?? "",
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"自定义命令执行失败：{ex.Message}", "右键菜单", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

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

    /// <param name="monitor">本窗口归属的显示器（工作区定位 + 持久 ID）。</param>
    /// <param name="fences">本屏 Fence 子集（host 按 MonitorAssignment 切分；孤儿 Fence 不进任何窗口）。</param>
    /// <param name="positions">本屏散落图标位置子集（启动排位缓存）。</param>
    public IconLayerWindow(MonitorInfo monitor, IReadOnlyList<FenceConfig> fences, IReadOnlyList<IconPosition> positions)
    {
        _monitorId = monitor.PersistentId;
        IsPrimary = monitor.IsPrimary;
        _workArea = (monitor.WorkX, monitor.WorkY, monitor.WorkWidth, monitor.WorkHeight);

        // P0-T2：DataTemplate 引用 {StaticResource FilePathToIconConverter}，须在 XAML 解析前注册。
        // 共享同一份 IconExtractor（与所有 FenceControl 共用图标缓存）。
        Resources["FilePathToIconConverter"] = new FilePathToIconConverter(_icons);

        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            // M3：铺本屏工作区（不含任务栏）。单屏时代用 SystemParameters.WorkArea（仅主屏），多屏逐屏定位。
            Left = _workArea.X; Top = _workArea.Y; Width = _workArea.W; Height = _workArea.H;
            _hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            // M6 修闪屏：WM_MOUSEACTIVATE 返回 MA_NOACTIVATE——点击本窗口不提升 Z 序
            // （NOACTIVATE 只阻止抢焦点不阻止 Z 提升；跨屏点击曾把图标层抬高 → 看门狗重锚 → DWM 重组合闪屏）。
            // 文本输入态（BeginInput）例外：需要激活才能打字。
            System.Windows.Interop.HwndSource.FromHwnd(_hwnd)?.AddHook((h, msg, w, l, ref handled) =>
            {
                const int WM_MOUSEACTIVATE = 0x0021;
                if (msg == WM_MOUSEACTIVATE && !_inputActive)
                {
                    handled = true;
                    return new IntPtr(3); // MA_NOACTIVATE：不激活（保留鼠标消息正常传递）
                }
                return IntPtr.Zero;
            });
            // M6：WorkerW 子窗口——只设样式不置底（置底会压到壁纸子窗口之下）。
            var ex = WindowInterop.GetExtendedStyle(_hwnd);
            WindowInterop.SetExtendedStyle(_hwnd, ex | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            // 真机修复（图标层浮在文件夹窗口上面）：SourceInitialized 时窗口尚未真正 Show，
            // WPF 随后把新窗口插入 Z-order 顶部，上面的 SendToBottom 被覆盖。
            // 双重保障：① ContentRendered（窗口已可见）后再置底一次；
            // ② Activated 守卫：非输入态被意外激活（Alt+Tab/其他路径）立即压回底部。
            // 顶层形态下压回 = 请主进程 BottomPair（本窗口自行置底会沉到壁纸之下，见 EndInput 注释）。
            Activated += (_, _) =>
            {
                if (_inputActive) return; // BeginInput 期间有意前台化（键盘输入），不压底
                RequestReorder();
            };
        };

        // P0-T2：散落图标集合驱动 LooseItemsControl（XAML 里 DataTemplate/ItemContainerStyle 已就绪）。
        LooseItemsControl.ItemsSource = _looseIcons;
        // 散落图标右键菜单：Opening 时按当前菜单配置动态构建（M6 可配置右键菜单）。
        LooseItemsControl.ContextMenu = new ContextMenu();
        LooseItemsControl.ContextMenuOpening += (_, e) =>
        {
            if (_contextMenuIcon is null) { e.Handled = true; return; }
            FillIconMenu(LooseItemsControl.ContextMenu, _contextMenuIcon.FilePath);
        };

        // M3：本屏散落图标启动排位缓存（host 按归属切分注入）；后写赢（同 path 重复条目取最后一个）。
        foreach (var p in positions) _iconPositions[p.FilePath] = (p.X, p.Y);

        // M3：从 host 注入的本屏 Fence 子集加载（替换 T7 的 ConfigStore 直读）。
        // 加载阶段用 LoadIcons（不触发 IconAdded，避免 N*M 次宿主重渲），_fencedPaths 在此直接初始化。
        LoadFences(fences);

        // T6：画布空白右键 → 新建收纳盒。
        IconCanvas.ContextMenu = BuildCanvasContextMenu();

        // B2：框选（Preview 隧道：先于图标/空白自身的处理）。
        IconCanvas.PreviewMouseLeftButtonDown += Canvas_PreviewMouseLeftButtonDown;
        IconCanvas.PreviewMouseMove += Canvas_PreviewMouseMove;
        IconCanvas.PreviewMouseLeftButtonUp += Canvas_PreviewMouseLeftButtonUp;
    }

    /// <summary>M3 加载：逐个 CreateFence + LoadIcons + _fencedPaths 初始化。
    /// 容错：IconFilePaths 里已被用户删除的 path 用 IconPathFilter 跳过；单个坏 Fence 不中断其余。</summary>
    private void LoadFences(IReadOnlyList<FenceConfig> fenceConfigs)
    {
        foreach (var fc in fenceConfigs)
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
                Log.Warning(ex, "LoadFences：Fence {FenceId} 加载失败，跳过该 Fence（其余继续）", fc?.Id);
                continue;
            }
        }

    }

    /// <summary>M6 IPC：SetFences 指令入口（启动期加载本屏 Fence 子集）。</summary>
    public void ApplyFences(IReadOnlyList<FenceConfig> fenceConfigs) => LoadFences(fenceConfigs);

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

        // 排序方式（重排散落图标到网格；收纳盒内不动）
        var sort = new MenuItem { Header = "排序方式" };
        var byName = new MenuItem { Header = "名称" };
        byName.Click += (_, _) => SortLooseOrdered(_looseIcons
            .OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase));
        var byType = new MenuItem { Header = "项目类型" };
        byType.Click += (_, _) => SortLooseOrdered(_looseIcons
            .OrderBy(TypeCategoryRank)
            .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase));
        sort.Items.Add(byName);
        sort.Items.Add(byType);
        menu.Items.Add(sort);

        // 对齐图标：保持当前排列顺序，仅吸附到标准网格（歪斜归位，不重排）
        var miAlign = new MenuItem { Header = "对齐图标" };
        miAlign.Click += (_, _) => AlignLooseToGrid();
        menu.Items.Add(miAlign);
        // 壁纸设置统一在托盘设置窗口（用户决策：桌面右键不再出现壁纸入口）。
        return menu;
    }

    /// <summary>散落图标就近吸附对齐：每个图标归位到最近的格点，冲突时挪最近空格。
    /// 按偏差升序落座——已在格位上的图标偏差为零最先锁定、纹丝不动；歪的图标就近归位。
    /// （真机教训：此前复用排序重排，顺序由 X 坐标微差决定 → 全体连锁移位，与用户"对齐"预期相反）。</summary>
    private void AlignLooseToGrid()
    {
        const double originX = 16, originY = 16;
        double stepX = IconSize <= 32 ? 90 : IconSize <= 48 ? 100 : 120;
        double stepY = IconSize <= 32 ? 96 : IconSize <= 48 ? 116 : 140;
        int maxRows = Math.Max(1, (int)((ActualHeight > 0 ? ActualHeight : SystemParameters.WorkArea.Height) - originY - 8) / (int)stepY);

        var plans = _looseIcons
            .Select(i =>
            {
                int col = Math.Max(0, (int)Math.Round((i.X - originX) / stepX));
                int row = Math.Max(0, Math.Min(maxRows - 1, (int)Math.Round((i.Y - originY) / stepY)));
                double dx = i.X - (originX + col * stepX), dy = i.Y - (originY + row * stepY);
                return (Item: i, Col: col, Row: row, Dist: dx * dx + dy * dy);
            })
            .OrderBy(p => p.Dist)
            .ToList();

        var taken = new HashSet<(int Col, int Row)>();
        foreach (var p in plans)
        {
            var cell = (p.Col, p.Row);
            if (taken.Contains(cell)) cell = NearestFreeCell(p.Col, p.Row, taken, maxRows);
            taken.Add(cell);
            p.Item.X = originX + cell.Col * stepX;
            p.Item.Y = originY + cell.Row * stepY;
        }
        RequestSave();
    }

    /// <summary>环形扩散找最近空格（列可无限向右扩，行受工作区限制）。</summary>
    private static (int Col, int Row) NearestFreeCell(int col, int row, HashSet<(int Col, int Row)> taken, int maxRows)
    {
        for (int r = 1; ; r++)
        {
            for (int dc = -r; dc <= r; dc++)
            for (int dr = -r; dr <= r; dr++)
            {
                if (Math.Max(Math.Abs(dc), Math.Abs(dr)) != r) continue; // 只扫当前环
                int c = col + dc, w = row + dr;
                if (c >= 0 && w >= 0 && w < maxRows && !taken.Contains((c, w))) return (c, w);
            }
        }
    }

    /// <summary>原生桌面同款类别权重：此电脑(0) → 回收站(1) → 文件夹(2) → 文件(3)。</summary>
    private static int TypeCategoryRank(IconItem i)
    {
        if (i.FilePath.StartsWith("::{20D04FE0", StringComparison.Ordinal)) return 0; // 此电脑
        if (i.FilePath.StartsWith("::", StringComparison.Ordinal)) return 1;           // 其他 shell 对象（回收站）
        if (Directory.Exists(i.FilePath)) return 2;                                     // 文件夹
        return 3;                                                                        // 文件
    }

    /// <summary>按给定顺序重排散落图标到网格（列优先）：全部先离格再按序回填，避免旧位干扰找空位。</summary>
    private void SortLooseOrdered(IEnumerable<IconItem> ordered)
    {
        var list = ordered.ToList();
        foreach (var i in _looseIcons) { i.X = -1; i.Y = -1; } // 先全离格（INPC 同步 UI）
        foreach (var i in list)
        {
            (i.X, i.Y) = FindFreeLooseSlot();
        }
        RequestSave(); // 布局上报（主进程防抖落盘）
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
        AuditReported?.Invoke("fence", "create", "新收纳盒", null);
        RequestSave(); // T7
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
        AuditReported?.Invoke("fence", "delete", title, null);
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
            if (it is null && (File.Exists(path) || Directory.Exists(path))) it = new IconItem(path, Path.GetFileName(path));
            if (it is not null && !_looseIcons.Contains(it)) AddLooseIcon(it); // X/Y<=0 网格排位，否则保留原位置；防重复
        }
        RequestSave(); // T7
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
        if (fenceChanged) RequestSave();

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
    /// 已定位项（X/Y&gt;0，如拖出回填保留原位、自由摆放拖到的位置）直接用原 X/Y。赋值触发 INPC → ItemContainerStyle 的
    /// Canvas.Left/Top 绑定刷新（T1 INPC 收益）。网格：10 列宽 90 / 行高 96，原点 (16,16)（M1 排版）。
    /// <para>真机修复（拖进拖出后重叠）：原先按 count 算 slot，拖入拖出后 _looseIcons 数量变化，count 算出的
    /// slot 可能与保留原 X/Y 的现有图标撞上。改遍历网格找第一个空闲 slot（与现有图标 X/Y 均差 &lt;1 视为占用）。</para>
    /// <para>自由摆放（位置优先级，X/Y 均&lt;=0 时）：① <see cref="_iconPositions"/> 命中 → 用持久化的 X/Y
    /// （重启保持）；② 否则 <see cref="FindFreeLooseSlot"/> 网格排位。</para></summary>
    private void AddLooseIcon(IconItem item)
    {
        // 仅当 X/Y 均 <=0（需自动排位）时定位；半定位（一轴 >0 一轴 <=0）实际不出现（IconItem 默认 0/0，
        // 回填/自由摆放保留双轴 >0），保守用“均 <=0”门控与原语义对齐。已定位项保留原 X/Y（如拖出回填、自由摆放）。
        if (item.X <= 0 && item.Y <= 0)
        {
            // 自由摆放：优先用持久化位置（重启保持），无记录才网格排位。
            if (_iconPositions.TryGetValue(item.FilePath, out var saved))
            {
                item.X = saved.X;
                item.Y = saved.Y;
            }
            else
            {
                (item.X, item.Y) = FindFreeLooseSlot();
            }
        }
        _looseIcons.Add(item);
    }

    /// <summary>遍历 10 列网格（col=0..9, row=0..递增）找第一个不与现有 _looseIcons 重叠的 slot。
    /// 重叠判定：包围盒相交（图标占 stepX×stepY 区域）——此前用格点精确匹配，自由摆放的图标
    /// 不在格点上 → 判不中 → 新图标全落 (16,16) 与已有重叠（真机：新装软件图标堆左上角）。</summary>
    private (double x, double y) FindFreeLooseSlot()
    {
        const double originX = 16, originY = 16;
        // 间距跟随图标尺寸档（M6 美化）：32→90x96 / 48→100x116 / 64→120x140
        double stepX = IconSize <= 32 ? 90 : IconSize <= 48 ? 100 : 120;
        double stepY = IconSize <= 32 ? 96 : IconSize <= 48 ? 116 : 140;
        // M6 修复：列优先（原生桌面同款）——行数受限于工作区高度，排满一列右移一列，
        // 永不排到窗口外（此前 10 列×无限行，图标多时排到 Y=1640 屏外被裁剪不可见）。
        int maxRows = Math.Max(1, (int)((ActualHeight > 0 ? ActualHeight : SystemParameters.WorkArea.Height) - originY - 8) / (int)stepY);
        for (int col = 0; ; col++)
        {
            for (int row = 0; row < maxRows; row++)
            {
                double x = originX + col * stepX;
                double y = originY + row * stepY;
                bool occupied = false;
                foreach (var existing in _looseIcons)
                {
                    // X<0 = 排序流程的"离格中"哨兵，不占位——(-1,-1) 的包围盒恰好与第一格相交，
                    // 不排除会把首个图标挤到第二格（真机：排序后左上角空一格）
                    if (existing.X >= 0 &&
                        existing.X < x + stepX && existing.X + stepX > x &&
                        existing.Y < y + stepY && existing.Y + stepY > y)
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
        RequestSave(); // T7：归属变化持久化
    }

    private void OnFenceIconRemoved(FenceControl fence, string filePath)
    {
        _fencedPaths.Remove(filePath);
        // 自由摆放：若由 IconCanvas_Drop（Fence 图标拖出空白）触发，_dropPosition 含 Drop 位置 → 回填落到 Drop 位置。
        // 读后立即清 null（一次性：防下次非 Drop 触发的 RemoveIcon 误用残留值，如 FenceControl 内部右键移除）。
        var dropPos = _dropPosition;
        _dropPosition = null;
        // T6：单条回填（替代 ApplySnapshot 全量兜底）。从 _allItems 找回 IconItem（T3 单实例 → 保留拖入前的原 X/Y 位置）；
        // 找不到（罕见：新文件被直接 fenced 且从未进散落区）则构造新项，AddLooseIcon 统一网格排位。
        var it = _allItems.FirstOrDefault(i => string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        // I-1 幽灵图标：fallback 前 File.Exists 守卫——fenced 文件被外部删除后 ApplyDiff 已移除该 path，
        // 此时拖出/删 Fence 触发回填，若不守卫会把磁盘上已不存在的文件加回散落区 → 幽灵图标（sync 不自清）。
        // 文件不存在则 it 保持 null，下方 Add 跳过（不加幽灵、不 NRE）。
        if (it is null && (File.Exists(filePath) || Directory.Exists(filePath))) it = new IconItem(filePath, Path.GetFileName(filePath));
        // 防重复事件：it 已在散落区则不重复 Add（Contains 走引用相等，T3 单实例下可靠）。
        if (it is not null && !_looseIcons.Contains(it))
        {
            // dropPos 命中 → 落到 Drop 位置（双轴 >0，AddLooseIcon 保留不重排）；否则 AddLooseIcon 按 _iconPositions/网格排。
            if (dropPos.HasValue)
            {
                it.X = dropPos.Value.X;
                it.Y = dropPos.Value.Y;
            }
            AddLooseIcon(it);
        }
        RequestSave(); // T7
    }

    /// <summary>T7：FenceControl 标题/折叠/拖动坐标变化 → 防抖持久化。</summary>
    private void OnFenceConfigChanged()
    {
        RequestSave();
    }

    // ---------- M3-T4：布局输出（持久化由 MultiMonitorHost 聚合） ----------

    /// <summary>布局变更通知：host 收到后防抖聚合所有窗口 + 孤儿配置落盘。
    /// 触发点：①归属变化（OnFenceIconAdded/Removed）；②新建/删除 Fence；③FenceControl.ConfigChanged；④自由摆放 Drop。</summary>
    private void RequestSave() => LayoutChanged?.Invoke();

    /// <summary>收集本屏布局（Fences + 散落位置），全部打上本屏 MonitorId 戳。UI 线程调用。
    /// 散落位置直接读 _looseIcons 现状（自由摆放拖动后 X/Y 已 INPC 更新），不依赖 _iconPositions 启动缓存。</summary>
    public (List<FenceConfig> Fences, List<IconPosition> Positions) BuildLayout()
    {
        var fences = _fences.Select(f => f.BuildConfig() with { MonitorId = _monitorId }).ToList();
        var positions = _looseIcons.Select(i => new IconPosition(i.FilePath, i.X, i.Y, _monitorId)).ToList();
        return (fences, positions);
    }

    /// <summary>path 是否归属本屏某 Fence（host 分发路由用）。</summary>
    public bool ContainsFenced(string path) => _fencedPaths.Contains(path);

    /// <summary>path 是否在本屏散落区（host 分发路由用）。</summary>
    public bool ContainsLoose(string path)
        => _looseIcons.Any(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase));

    // ---------- M3-T5：跨屏拖拽迁移 API（host 协调，源窗导出 + 目标窗导入） ----------

    /// <summary>Fence 本体被拖出本窗口边界 → 转 OLE 跨屏拖拽（FenceControl.HeaderBar_MouseMove 触发）。
    /// payload 仅 Text（"fence:&lt;id&gt;"）：explorer/文件夹不认 → 拖到窗口外无副作用；目标窗 IconCanvas_Drop 解析后走 host.TransferFence。</summary>
    public void BeginFenceCrossScreenDrag(FenceControl fence)
    {
        var data = new DataObject();
        data.SetData(DataFormats.Text, "fence:" + fence.BuildConfig().Id);
        DragDrop.DoDragDrop(fence, data, DragDropEffects.Move);
        // Drop 处理在目标窗口（或本窗同屏移动分支）；拖回/拖到无效处则现状即最终态，兜底存一次。
        RequestSave();
    }

    /// <summary>本窗是否含指定 Id 的 Fence（host 路由用）。</summary>
    public bool ContainsFence(string fenceId) => _fences.Any(f => f.BuildConfig().Id == fenceId);

    /// <summary>导出图标供跨屏迁移：fenced → 静默清归属（不回填散落区）；loose → 从散落区移除。
    /// 返回 IconItem（文件已删则 null）；同时从 _allItems 移除（源窗不留回填源）。</summary>
    public IconItem? ExportIcon(string path)
    {
        if (_fencedPaths.Contains(path))
        {
            foreach (var f in _fences.Where(f => f.ContainsIcon(path))) f.RemoveIconSilent(path);
            _fencedPaths.Remove(path);
        }
        else
        {
            var loose = _looseIcons.FirstOrDefault(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (loose is null) return null; // 本窗根本不持有（防御）
            _looseIcons.Remove(loose);
        }
        var item = _allItems.FirstOrDefault(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (item is null && File.Exists(path)) item = new IconItem(path, Path.GetFileName(path));
        if (item is null) return null;
        _allItems = _allItems.Where(i => !string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase)).ToList();
        RequestSave();
        return item;
    }

    /// <summary>接收另一屏迁来的图标 → 落散落区 Drop 位置（本地坐标直接可用）。</summary>
    public void ImportLoose(IconItem item, Point pos)
    {
        item.X = pos.X;
        item.Y = pos.Y;
        _iconPositions[item.FilePath] = (pos.X, pos.Y);
        if (!_looseIcons.Contains(item)) _looseIcons.Add(item);
        if (!_allItems.Any(i => string.Equals(i.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)))
            _allItems = _allItems.Append(item).ToList();
        RequestSave();
    }

    /// <summary>静默移除 Fence（无确认框、图标不回填散落区——随 Fence 迁走），返回其 config 供目标窗重建。</summary>
    public FenceConfig? ExportFence(string fenceId)
    {
        var fence = _fences.FirstOrDefault(f => f.BuildConfig().Id == fenceId);
        if (fence is null) return null;
        var cfg = fence.BuildConfig();
        _fences.Remove(fence);
        IconCanvas.Children.Remove(fence);
        fence.IconAdded -= OnFenceIconAdded;
        fence.IconRemoved -= OnFenceIconRemoved;
        fence.ConfigChanged -= OnFenceConfigChanged;
        foreach (var p in cfg.IconFilePaths) _fencedPaths.Remove(p);
        RequestSave();
        return cfg;
    }

    /// <summary>接收另一屏迁来的 Fence：重建控件 + 恢复归属图标（X/Y 已由 host 设为 Drop 位置）。</summary>
    public void ImportFence(FenceConfig cfg)
    {
        var fence = CreateFence(cfg with { MonitorId = _monitorId });
        var existing = IconPathFilter.FilterExisting(cfg.IconFilePaths);
        fence.LoadIcons(existing);
        foreach (var p in existing) _fencedPaths.Add(p);
        RequestSave();
    }

    /// <summary>同窗 Fence 拖动换位置（TransferFence 同屏分支）。</summary>
    public void MoveFence(string fenceId, Point pos)
    {
        var fence = _fences.FirstOrDefault(f => f.BuildConfig().Id == fenceId);
        if (fence is null) return;
        Canvas.SetLeft(fence, pos.X);
        Canvas.SetTop(fence, pos.Y);
        RequestSave();
    }

    // ---------- M2 真机修复 Bug 2：IInteractiveHost 实现 ----------
    // IconLayerWindow 全屏 NOACTIVATE：不抢 explorer 焦点（M1 设计）。但导致 app 内 TextBox 都打不出字。
    // BeginInput 临时去 NOACTIVATE + 前台化；EndInput 恢复 NOACTIVATE + SendToBottom 回桌面层。
    // 严格成对：FenceControl.BeginTitleEdit/EndTitleEdit、RenameDialog.AskRename 的 try/finally 保证。

    /// <summary>输入态结束/意外激活后请求主进程重排 Z 序（BottomPair：图标层置底 + 壁纸插其下）。
    /// 本窗口不能自行 SendToBottom——owned 窗口置底会沉到壁纸之下（真机：改名回车后全部图标消失）。</summary>
    public static event Action? ReorderRequested;

    private static void RequestReorder() => ReorderRequested?.Invoke();

    /// <summary>临时去 WS_EX_NOACTIVATE 并前台化，让 app 内 TextBox 可接收键盘输入。</summary>
    public void BeginInput()
    {
        if (_inputActive) return; // 防重入：已在输入态时不再覆盖 _noActivatePrevEx（避免丢失原快照）
        if (_hwnd == IntPtr.Zero) return; // SourceInitialized 未跑（极端早调用）：静默放弃，TextBox 仍可能可输入
        _noActivatePrevEx = WindowInterop.EnableActivation(_hwnd);
        _inputActive = true;
    }

    /// <summary>恢复 WS_EX_NOACTIVATE（不动 Z 序），Z 序压回桌面层由主进程 BottomPair 完成。</summary>
    public void EndInput()
    {
        if (!_inputActive) return; // 与 BeginInput 未配对（如 BeginInput 早返回）：no-op 防误恢复
        if (_hwnd != IntPtr.Zero)
        {
            // 不能用 RestoreNonInteractive（含 SendToBottom）：owned 窗口置底会压到壁纸之下
            // → 桌面图标全消失（真机踩坑 ×2：系统菜单弹窗恢复、收纳盒改名回车）。
            WindowInterop.RestoreNoActivateStyle(_hwnd, _noActivatePrevEx);
        }
        _inputActive = false;
        RequestReorder(); // 前台化把窗口顶到了 Z 序顶部，交主进程配对压回
    }

    // ---------- T3：画布空白 Drop（从 Fence 内容区拖出到空白） ----------

    // 拖拽诊断：DragOver 高频触发，formats 摘要变化时才记一条（新拖入会话的首条即可定位问题）
    private string? _lastDragFormats;

    private void IconCanvas_DragOver(object sender, DragEventArgs e)
    {
        // FileDrop（外部文件从 explorer/文件夹拖入）或 Text（app 图标拖出空白）都接受 Move；其他 None。
        // M2 真机修复前仅认 Text，导致外部文件拖到桌面无反馈、Drop 不处理 → 文件进不了桌面。
        e.Effects = (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;

        var fmt = string.Join(",", e.Data.GetFormats());
        if (!string.Equals(fmt, _lastDragFormats, StringComparison.Ordinal))
        {
            _lastDragFormats = fmt;
            Log.Debug("拖入会话 DragOver：formats=[{Formats}] FileDrop={HasFileDrop} Text={HasText}",
                fmt, e.Data.GetDataPresent(DataFormats.FileDrop), e.Data.GetDataPresent(DataFormats.Text));
        }
    }

    private void IconCanvas_Drop(object sender, DragEventArgs e)
    {
        // 优先级：Text present → app 图标拖出空白（app 图标 DataObject 含 Text+FileDrop，但语义是
        //   "从 Fence 移除归属回散落区"，文件本身已在桌面不应移动 → 走 Text 分支，不触发 FileDrop 移动）。
        // 否则 FileDrop present → 外部文件移到桌面（explorer/文件夹拖入，仅 FileDrop 无 Text）。
        // 两者都 present 必为 app 图标（见 Loose_PreviewMouseMove / FenceControl MouseMove 构造的 DataObject）。
        // B2 多选拖动摆放：以 Drop 点为起点网格展开（区别于外部文件移入）。
        if (e.Data.GetDataPresent("DMSelection") &&
            e.Data.GetData(DataFormats.FileDrop) is string[] selPaths)
        {
            var pos0 = e.GetPosition(LooseItemsControl);
            double stepX = IconSize <= 32 ? 90 : IconSize <= 48 ? 100 : 120;
            double stepY = IconSize <= 32 ? 96 : IconSize <= 48 ? 116 : 140;
            int col = 0, row = 0;
            foreach (var sp in selPaths)
            {
                var it = _looseIcons.FirstOrDefault(i => string.Equals(i.FilePath, sp, StringComparison.OrdinalIgnoreCase));
                if (it is not null)
                {
                    it.X = pos0.X + col * stepX;
                    it.Y = pos0.Y + row * stepY;
                    _iconPositions[sp] = (it.X, it.Y);
                }
                if (++col >= 10) { col = 0; row++; }
            }
            RequestSave();
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.Text))
        {
            var path = (string)e.Data.GetData(DataFormats.Text);
            // Drop 位置用 LooseItemsControl 坐标系（ItemsPanel Canvas 与 IconCanvas 1:1，见 XAML 注释）= IconItem.X/Y 同坐标系。
            var pos = e.GetPosition(LooseItemsControl);

            // M3-T5：Fence 本体跨屏拖拽（payload "fence:<id>"，见 BeginFenceCrossScreenDrag）。
            if (path.StartsWith("fence:", StringComparison.Ordinal))
            {
                var fid = path["fence:".Length..];
                if (ContainsFence(fid)) MoveFence(fid, pos);   // 同窗拖动 = 仅换位置
                else Host?.TransferFence(fid, pos);            // 跨屏 → 主进程中转
                e.Handled = true;
                return;
            }

            var owner = _fences.FirstOrDefault(f => f.ContainsIcon(path));
            if (owner is not null)
            {
                // Fence 图标拖出空白：记 _dropPosition，RemoveIcon 触发 OnFenceIconRemoved 用它回填（落到 Drop 位置）。
                _dropPosition = pos;
                owner.RemoveIcon(path);
            }
            else
            {
                // 自由摆放：散落图标（owner null）拖到空白 = 改位置（像原生桌面自由摆放）。
                // INPC setter 触发 → Canvas.Left/Top 绑定更新 → 图标移到 Drop 位置。SaveFencesDebounced 持久化新位置。
                var loose = _looseIcons.FirstOrDefault(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase));
                if (loose is not null)
                {
                    loose.X = pos.X;
                    loose.Y = pos.Y;
                    // 同步内存缓存（保持与 _looseIcons 一致）：消除"用户拖到 B 后文件删+还原同路径 → AddLooseIcon 用陈旧缓存 A"边界。
                    _iconPositions[path] = (pos.X, pos.Y);
                    RequestSave();
                }
                else
                {
                    // M3-T5：跨屏图标拖拽——path 属另一窗口的 Fence/散落区（本窗查无）→ 经 host 迁移到本屏散落区。
                    Host?.TransferLoose(path, pos);
                }
            }
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            Log.Debug("Drop FileDrop 分支：{Count} 项 [{First}]", files?.Length ?? 0, files is { Length: > 0 } ? files[0] : "");
            if (files is not null && files.Length > 0) MoveExternalFilesToDesktop(files);
            e.Handled = true;
            return;
        }

        Log.Debug("Drop 无处理格式：formats=[{Formats}]", string.Join(",", e.Data.GetFormats()));
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
            try
            {
                if (Directory.Exists(p.Source))
                {
                    // 目录拖入：同卷 Directory.Move；跨卷 IOException 降级递归复制+删源（对齐文件路径的降级策略）
                    try { Directory.Move(p.Source, p.Target); }
                    catch (IOException) { CopyDirectory(p.Source, p.Target); Directory.Delete(p.Source, recursive: true); }
                }
                else
                {
                    // 跨卷 File.Move 抛 IOException → 降级 Copy + Delete（与 explorer 跨卷默认"复制"语义对齐，
                    // 但本工具按 DragDropEffects.Move 语义最终删除源，符合用户从文件夹"移到桌面"的预期）。
                    try { File.Move(p.Source, p.Target); }
                    catch (IOException) { File.Copy(p.Source, p.Target); File.Delete(p.Source); }
                }
            }
            catch (Exception ex)
            {
                // 单文件失败不中断其余（权限/占用/源只读等）。FSW 后续对账兜底；错误可见于日志便于诊断。
                Log.Warning(ex, "IconCanvas_Drop：移动外部文件 {Source} -> {Target} 失败", p.Source, p.Target);
            }
        }
    }

    /// <summary>递归复制目录（目录拖入跨卷降级用）。</summary>
    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    // ---------- B2：框选多选 ----------
    // 空白按下并拖动超阈值 → 半透明选择矩形 → 松开结算（相交的散落图标多选；替换式选择）。
    private System.Windows.Shapes.Rectangle? _marquee;
    private Point? _marqueeOrigin;
    private bool _marqueeDragging;

    private void EnsureMarquee()
    {
        if (_marquee is not null) return;
        _marquee = new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0x66, 0xCC, 0xFF)),
            Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x66, 0xCC, 0xFF)),
            StrokeThickness = 1,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        IconCanvas.Children.Add(_marquee);
    }

    /// <summary>空白按下：记录框选起点（单击/双击语义保留在 MouseLeftButtonDown/双击分支）。</summary>
    private void Canvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, IconCanvas)) return; // 仅空白起点
        if (e.ClickCount >= 2) return;                              // 双击交给显隐分支
        _marqueeOrigin = e.GetPosition(IconCanvas);
        _marqueeDragging = false;
    }

    private void Canvas_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_marqueeOrigin is not { } origin || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(IconCanvas);
        if (!_marqueeDragging &&
            (Math.Abs(pos.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance ||
             Math.Abs(pos.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance)) return;
        _marqueeDragging = true;
        EnsureMarquee();
        _marquee!.Visibility = Visibility.Visible;
        var x = Math.Min(origin.X, pos.X); var y = Math.Min(origin.Y, pos.Y);
        var w = Math.Abs(pos.X - origin.X); var h = Math.Abs(pos.Y - origin.Y);
        Canvas.SetLeft(_marquee, x); Canvas.SetTop(_marquee, y);
        _marquee.Width = w; _marquee.Height = h;
        // 实时预览：拖动中相交的图标立即高亮（原生桌面同款体验；松手结算为最终态）。
        var live = new Rect(x, y, w, h);
        double cw = LabelWidth, ch = IconSize + 44;
        foreach (var i in _looseIcons)
            i.IsSelected = live.IntersectsWith(new Rect(i.X, i.Y, cw, ch));
        e.Handled = true;
    }

    /// <summary>框选结算：矩形与散落图标 cell 相交 → 多选（替换式）；单击（未拖动）→ 清选中（原有语义）。</summary>
    private void Canvas_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_marqueeOrigin is not { } origin) return;
        if (_marqueeDragging && _marquee is not null)
        {
            var pos = e.GetPosition(IconCanvas);
            var sel = new Rect(
                Math.Min(origin.X, pos.X), Math.Min(origin.Y, pos.Y),
                Math.Abs(pos.X - origin.X), Math.Abs(pos.Y - origin.Y));
            _marquee.Visibility = Visibility.Collapsed;
            double cw = LabelWidth, ch = IconSize + 44; // cell 尺寸估算（图标+标签）
            foreach (var i in _looseIcons)
                i.IsSelected = sel.IntersectsWith(new Rect(i.X, i.Y, cw, ch));
        }
        else
        {
            ClearLocalSelection(); // 空白单击：清选中（保持原语义）
        }
        _marqueeOrigin = null;
        _marqueeDragging = false;
    }

    /// <summary>当前多选集（拖动/批量操作用）。</summary>
    private List<IconItem> SelectedIcons => _looseIcons.Where(i => i.IsSelected).ToList();

    // ---------- B1：拖到回收站删除 ----------

    /// <summary>拖拽经过图标项：仅回收站（shell 虚拟对象）接受"删除"落点。</summary>
    private void IconItem_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && IsRecycleBinItem(e.OriginalSource))
            e.Effects = DragDropEffects.Move;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>落到回收站项：文件/文件夹删除进回收站（确认后执行；FSW 自动同步图标消失）。</summary>
    private void IconItem_Drop(object sender, DragEventArgs e)
    {
        if (!IsRecycleBinItem(e.OriginalSource)) { e.Handled = true; return; }
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Handled = true; return; }
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files is not { Length: > 0 }) { e.Handled = true; return; }

        var names = string.Join(Environment.NewLine, files.Select(Path.GetFileName));
        if (MessageBox.Show($"确定将以下 {files.Length} 个项目移到回收站？\n\n{names}",
                "删除确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            e.Handled = true;
            return;
        }
        foreach (var f in files)
        {
            try
            {
                if (Directory.Exists(f))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(f,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"删除失败：{Path.GetFileName(f)}\n{ex.Message}", "删除", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        e.Handled = true; // 阻止冒泡到画布 Drop（那会当"移动到桌面"处理）
    }

    /// <summary>视觉树回溯 DataContext：是否回收站虚拟项。</summary>
    private static bool IsRecycleBinItem(object? source)
    {
        var el = source as DependencyObject;
        while (el is not null)
        {
            if (el is FrameworkElement fe && fe.DataContext is IconItem item)
                return item.FilePath.StartsWith("::{645FF040", StringComparison.Ordinal);
            el = VisualTreeHelper.GetParent(el);
        }
        return false;
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
        ClearAllSelection(); // M5-UI：点空白清除选中
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

    /// <summary>双击/右键打开回调（App 上报 IconOpened/Error，主进程日志可见）。</summary>
    public static event Action<string, string?>? OpenReported;

    /// <summary>操作审计上报（App 转 IPC FenceAction/IconAction，主进程落库）。</summary>
    public static event Action<string, string, string, string?>? AuditReported;


    private static void Open(string path)
    {
        try
        {
            // WorkingDirectory=文件所在目录：.lnk 的目标若用相对路径，进程 cwd 在 bin 下会解析失败
            //（真机：Discord.lnk 打开报"找不到路径"）。
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            });
            OpenReported?.Invoke(path, null);
        }
        catch (Exception ex)
        {
            OpenReported?.Invoke(path, ex.Message); // M6：失败必须可见（曾静默吞掉）
        }
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
        // B2 修：按在多选集（≥2）上 → 保留多选（拖动将带全组）；否则按原有单选逻辑。
        var pressed = SelectedIcons;
        if (!(pressed.Count >= 2 && pressed.Any(i => i.FilePath == icon.FilePath)))
            SelectLoose(icon); // M5-UI：单击选中高亮
    }

    /// <summary>M5-UI：单选高亮（先全局清除，再选中自己）。</summary>
    private void SelectLoose(IconItem icon)
    {
        // 单选：先清本屏全部选中（含旧选中），再请其他屏清（IPC），最后选中新图标。
        // M6 修复：本屏自清不能依赖主进程回发（回发会清掉刚选中的新图标，竞态）。
        ClearLocalSelection();
        Host?.ClearAllSelection();
        icon.IsSelected = true;
    }

    /// <summary>M5-UI：清除全部选中态（跨屏单选：通过 Host 全局清除）。</summary>
    public void ClearAllSelection()
    {
        Host?.ClearAllSelection();
    }

    /// <summary>M5-UI：清除本屏幕的选中态（散落 + 收纳盒）。</summary>
    public void ClearLocalSelection()
    {
        // 清除散落图标选中
        foreach (var i in _looseIcons)
        {
            if (i.IsSelected)
            {
                i.IsSelected = false;
            }
        }
        // 清除所有收纳盒的选中
        foreach (var f in _fences)
        {
            f.ClearIconSelection();
        }
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
            // B2 多选拖动：被拖项在选中集（≥2）→ FileDrop 带全部选中项 + DMSelection 标记
            var selection = SelectedIcons;
            bool multi = selection.Count >= 2 && selection.Any(i => i.FilePath == path);
            // M2 真机修复：DataObject 同时含 FileDrop（explorer/文件夹认这个 + 系统拖拽视觉反馈）
            // + Text（兼容 Fence_Drop / IconCanvas_Drop 按 Text 读 app 图标归属）。
            // 单纯 Text 格式 explorer 不认 → 拖到文件夹无反应且无拖拽图标反馈。
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, multi ? selection.Select(i => i.FilePath).ToArray() : new[] { path });
            data.SetData(DataFormats.Text, path);
            if (multi) data.SetData("DMSelection", true); // 内部多选拖动标记（目标端区分外部文件拖入）
            // 拖源用 LooseItemsControl（根 ItemsControl，稳定不回收）。
            DragDrop.DoDragDrop(LooseItemsControl, data, DragDropEffects.Move);
        }
    }

    /// <summary>右键按下时 hit-test 捕获目标图标（供 ContextMenu 四项 Click 复用）。不设 Handled → ContextMenu 正常打开。</summary>
    private void Loose_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _contextMenuIcon = FindIconFromSource(e.OriginalSource);
    }


    /// <summary>B2 批量删除（确认 → 回收站；目录/文件分流）。</summary>
    private void BatchDelete(List<string> paths)
    {
        var names = string.Join(Environment.NewLine, paths.Select(Path.GetFileName));
        if (MessageBox.Show($"确定将以下 {paths.Count} 个项目移到回收站？\n\n{names}",
                "批量删除", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        foreach (var f in paths)
        {
            try
            {
                if (Directory.Exists(f))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(f,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("批量删除失败: " + f + " " + ex.Message);
            }
        }
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
            AuditReported?.Invoke("icon", "rename", $"{Path.GetFileName(oldPath)} → {Path.GetFileName(result.NewPath!)}", oldPath);
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

            // 文件夹走 DeleteDirectory（M6：桌面文件夹也是图标；DeleteFile 对目录抛异常）
            if (Directory.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            AuditReported?.Invoke("icon", "delete", name, path);
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
