using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopManager.App.Controls;
using DesktopManager.App.Services;
using DesktopManager.Core.Models;
using DesktopManager.Core.Services;
using DesktopManager.Native;

namespace DesktopManager.App.Windows;

public partial class IconLayerWindow : Window
{
    private readonly IconExtractor _icons = new();

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
    // SetIcons 全量重渲时会 Clear IconCanvas，Fence 必须在 Clear 后重 Add 才不丢（实例状态在内存）。
    private readonly List<FenceControl> _fences = new();
    // 当前已归属任一 Fence 的 FilePath 集合，SetIcons 据此过滤散落区（避免归属图标重复显示）。
    private readonly HashSet<string> _fencedPaths = new(StringComparer.OrdinalIgnoreCase);
    // 最近一次 SetIcons 的输入快照；归属变化后用它重渲散落区（保证过滤生效）。
    private IReadOnlyList<IconItem> _allItems = Array.Empty<IconItem>();
    // 散落图标项拖出候选状态（移动阈值检测，避免与双击 Open 冲突）。
    private bool _iconDragArmed;
    private string? _iconDragPath;
    private Point _iconDragOrigin;

    // ---------- T4：双击空白切可见性 ----------
    // 全部散落图标 + 所有 FenceControl 的可见性开关。SetIcons 全量重渲时按此值设新元素 Visibility，保持隐藏状态跨重渲。
    private bool _iconsVisible = true;

    /// <param name="store">T7 注入的配置存储。null 仅用于设计器/极端场景（无持久化）。</param>
    public IconLayerWindow(IConfigStore? store = null)
    {
        InitializeComponent();
        _store = store ?? new NullConfigStore();
        SourceInitialized += (_, _) =>
        {
            // 铺主屏工作区（不含任务栏），避免遮挡任务栏。M3 多屏改按显示器工作区定位。
            var work = SystemParameters.WorkArea;
            Left = work.Left; Top = work.Top; Width = work.Width; Height = work.Height;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            WindowInterop.MakeNonInteractiveTopmost(hwnd); // 不点击穿透，可点图标
        };

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
            System.Diagnostics.Debug.WriteLine($"LoadFencesFromConfig: Load 失败，空配置启动：{ex}");
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
                System.Diagnostics.Debug.WriteLine($"LoadFencesFromConfig：Fence {fc?.Id} 加载失败，跳过该 Fence（其余继续）：{ex}");
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

    /// <summary>删除本 Fence：确认 → 从 _fences 移除 + 画布移除 + 取消订阅 + 清理 _fencedPaths（图标回散落区）→ 重渲。
    /// 关键：该 Fence 的 IconFilePaths 从 _fencedPaths 移除前，先检查无其他 Fence 仍归属（防跨 Fence 单归属误删）。</summary>
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
        // 释放归属：该 Fence 的图标若无其他 Fence 仍持有，从 _fencedPaths 移除 → SetIcons 后回散落区。
        foreach (var path in paths)
        {
            if (!_fences.Any(f => f.ContainsIcon(path)))
                _fencedPaths.Remove(path);
        }
        SetIcons(_allItems); // 重渲散落区，被释放的图标回来
        SaveFencesDebounced(); // T7
    }

    /// <summary>渲染散落图标列表（M1 单屏：简单网格排列，X/Y 来自 IconItem 或自动排）。
    /// T3 关键协调：散落区排除「已归属任一 Fence」的 FilePath；FenceControl 在 Clear 后重 Add（状态保留）。
    /// 被 DesktopSync.Changed 全量重渲调用时归属划分得以保持。</summary>
    public void SetIcons(IReadOnlyList<IconItem> items)
    {
        _allItems = items;
        IconCanvas.Children.Clear();
        // T4 协调：重 Add/重建的元素必须按当前 _iconsVisible 设 Visibility，否则 Sync/拖拽触发的重渲会把隐藏的图标冒回来。
        var vis = _iconsVisible ? Visibility.Visible : Visibility.Collapsed;
        // FenceControl 实例状态（含 ContentArea 归属图标）在内存；Clear 只断开视觉树，重 Add 后保留。
        foreach (var f in _fences)
        {
            f.Visibility = vis;
            IconCanvas.Children.Add(f);
        }

        int col = 0, row = 0;
        foreach (var item in items)
        {
            if (_fencedPaths.Contains(item.FilePath)) continue; // 已归属，不显示在散落区

            var img = new Image
            {
                Width = 32, Height = 32,
                Source = _icons.GetIcon(item.FilePath),
                Stretch = Stretch.Uniform
            };
            var label = new TextBlock
            {
                Text = item.DisplayName,
                MaxWidth = 80,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
                Padding = new Thickness(2, 0, 2, 0)
            };
            var panel = new StackPanel { Width = 80 };
            panel.Children.Add(img);
            panel.Children.Add(label);
            // T4 协调：新建散落 panel 默认 Visible，隐藏状态下必须显式 Collapsed。
            panel.Visibility = vis;

            double x = item.X > 0 ? item.X : 16 + col * 90;
            double y = item.Y > 0 ? item.Y : 16 + row * 96;
            Canvas.SetLeft(panel, x);
            Canvas.SetTop(panel, y);
            panel.Tag = item.FilePath;
            // T5：散落图标右键菜单（四项）。右键(MouseRightButton/ContextMenu)与左键双击Open/左键拖拽是不同事件，不冲突。
            panel.ContextMenu = BuildIconContextMenu(item.FilePath);
            panel.MouseLeftButtonDown += (_, e) =>
            {
                // 双击 Open（保留 M1 行为）；单击 arm 拖拽候选。双击不 arm，避免双击后误触发拖拽。
                if (e is MouseButtonEventArgs m && m.ClickCount >= 2)
                {
                    // review-finding 1：双击 = 两次 down，第一次(ClickCount=1)已 arm。
                    // 若不清零，双击第二次 down 后保持按住并移动 → MouseMove 满足 armed+Pressed+超阈值 → 误触 DoDragDrop。
                    _iconDragArmed = false;
                    _iconDragPath = null;
                    Open((string)panel.Tag);
                    return;
                }
                _iconDragArmed = true;
                _iconDragPath = (string)panel.Tag;
                _iconDragOrigin = e.GetPosition(this);
            };
            // review-finding 2：单击松手未移动 → 清 armed（与 FenceControl 内容图标 img.MouseLeftButtonUp 对称，
            // 避免 armed 残留到下次 down 叠加误触）。
            panel.MouseLeftButtonUp += (_, _) =>
            {
                _iconDragArmed = false;
                _iconDragPath = null;
            };
            // 拖出：左键按下且移动超阈值 → DoDragDrop（data=FilePath 字符串，DragDropEffects.Move）。
            panel.MouseMove += (_, e) =>
            {
                if (!_iconDragArmed || _iconDragPath is null) return;
                if (e.LeftButton != MouseButtonState.Pressed) return;
                var pos = e.GetPosition(this);
                if (Math.Abs(pos.X - _iconDragOrigin.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(pos.Y - _iconDragOrigin.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    var path = _iconDragPath;
                    _iconDragArmed = false;
                    _iconDragPath = null;
                    DragDrop.DoDragDrop(panel, path, DragDropEffects.Move);
                }
            };
            IconCanvas.Children.Add(panel);

            if (++col >= 10) { col = 0; row++; }
        }
    }

    // ---------- T3：Fence 归属事件回调 ----------

    private void OnFenceIconAdded(FenceControl fence, string filePath)
    {
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
        // 异步重渲散落区：不在 Drop 回调里同步改视觉树（避免事件源元素正被重渲的隐患，同 App.xaml.cs I-5 模式）。
        Dispatcher.BeginInvoke(new Action(() => SetIcons(_allItems)));
        SaveFencesDebounced(); // T7：归属变化持久化
    }

    private void OnFenceIconRemoved(FenceControl fence, string filePath)
    {
        _fencedPaths.Remove(filePath);
        Dispatcher.BeginInvoke(new Action(() => SetIcons(_allItems)));
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
            System.Diagnostics.Debug.WriteLine($"防抖保存失败：{ex}");
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
            System.Diagnostics.Debug.WriteLine($"退出保存失败：{ex}");
        }
    }

    /// <summary>设计器/极端场景（未注入 store）的 no-op 实现，避免 null 检查散落。</summary>
    private sealed class NullConfigStore : IConfigStore
    {
        public AppConfig Load() => new AppConfig();
        public void Save(AppConfig config) { /* no-op */ }
    }

    // ---------- T3：画布空白 Drop（从 Fence 内容区拖出到空白） ----------

    private void IconCanvas_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void IconCanvas_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.Text)) { e.Handled = true; return; }
        var path = (string)e.Data.GetData(DataFormats.Text);
        // 定位该 FilePath 的归属 Fence；从其移除（触发 IconRemoved → 异步重渲散落区，图标自动回填）。
        // 若 path 不属于任何 Fence（散落图标拖到空白），无 owner，什么都不做。
        var owner = _fences.FirstOrDefault(f => f.ContainsIcon(path));
        owner?.RemoveIcon(path);
        e.Handled = true;
    }

    // ---------- T4：双击画布空白切可见性 ----------

    private void IconCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        // hit-test：只有命中画布空白本身（OriginalSource 是 IconCanvas）才触发隐藏/显示。
        // 点散落图标（StackPanel/Image/TextBlock）或 FenceControl 子元素时，OriginalSource 是这些子元素而非 Canvas，
        // 不触发本逻辑（交给图标的 panel 双击 Open / 盒子的交互）。Background=Transparent 使空白可被命中（null 背景不响应）。
        if (!ReferenceEquals(e.OriginalSource, IconCanvas)) return;
        _iconsVisible = !_iconsVisible;
        var vis = _iconsVisible ? Visibility.Visible : Visibility.Collapsed;
        // IconCanvas 直接子元素只有散落图标 StackPanel 和 FenceControl；只切这两类，不触碰其他可能元素。
        foreach (UIElement child in IconCanvas.Children)
        {
            if (child is StackPanel or FenceControl)
                child.Visibility = vis;
        }
        e.Handled = true;
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* M1 真机验收记录失败 case */ }
    }

    // ---------- T5：散落图标右键菜单（打开 / 重命名 / 删除 / 打开文件位置） ----------

    /// <summary>构造散落图标右键菜单。只作用于散落区（FenceControl 内容图标右键不在本任务）。</summary>
    private ContextMenu BuildIconContextMenu(string path)
    {
        var menu = new ContextMenu();
        var miOpen = new MenuItem { Header = "打开" };
        miOpen.Click += (_, _) => Open(path);
        var miRename = new MenuItem { Header = "重命名" };
        miRename.Click += (_, _) => RenameIcon(path);
        var miDelete = new MenuItem { Header = "删除" };
        miDelete.Click += (_, _) => DeleteIcon(path);
        var miLocate = new MenuItem { Header = "打开文件位置" };
        miLocate.Click += (_, _) => OpenFileLocation(path);
        menu.Items.Add(miOpen);
        menu.Items.Add(miRename);
        menu.Items.Add(miDelete);
        menu.Items.Add(miLocate);
        return menu;
    }

    /// <summary>重命名：自定义对话框（预填完整文件名）→ ResolveRenamePath 校验 → File.Move。
    /// 成功后 DesktopSync 的 FSW 自动检测 Renamed → Changed → SetIcons 全量重渲，UI 自动同步（旧名消失、新名出现）。</summary>
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
            // 不手动刷新 IconLayer：DesktopSync.Changed 已在 App.xaml.cs 接到 SetIcons，FSW 会触发重渲。
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
            // 不手动刷新：DesktopSync.Changed → SetIcons 自动移除该图标。
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
