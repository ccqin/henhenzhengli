using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopManager.Player.Icons;
using DesktopManager.Core.Models;

namespace DesktopManager.Player.Icons;

/// <summary>
/// 收纳盒控件（Fences 风格）：半透明、可整体拖动、可折叠、标题可编辑的盒子。
/// T2 实现盒子本身；T3 加图标拖入/拖出 + 归属渲染（内存）；右键菜单（T6）、从 ConfigStore 加载/持久化（T7）不在本任务。
/// 拖动依赖父容器为 Canvas（T7 会把本控件加到 IconLayerWindow.IconCanvas）；未挂画布时拖动为 no-op，不崩。
/// </summary>
public partial class FenceControl : UserControl
{
    private FenceConfig _config = new();
    private string _title = "";
    private bool _folded;
    private bool _isEditing;
    private bool _isDragging;
    private static readonly SolidColorBrush SelectedBrush = new(Color.FromArgb(0x40, 0x66, 0xCC, 0xFF));
    private Point _dragOrigin;     // 按下时鼠标相对父 Canvas 的位置
    private double _startLeft;     // 按下时控件在父 Canvas 的 Left
    private double _startTop;      // 按下时控件在父 Canvas 的 Top

    // ---------- T3：归属图标渲染 ----------
    // T7：IconExtractor 改为可由宿主注入共享实例（跨 Fence 共用一份图标缓存）。
    // 保留默认 new() 兼容 XAML 设计器/独立 spike 场景；宿主接线时在 Bind 前 set。
    private IconExtractor _icons = new();
    // FilePath → 内容区图标 UI 元素，用于去重 / O(1) 移除。
    private readonly Dictionary<string, FrameworkElement> _contentIcons = new(StringComparer.OrdinalIgnoreCase);
    // 内容图标拖出候选状态（移动阈值检测，与散落区图标拖出同模式）。
    private bool _contentDragArmed;
    private string? _contentDragPath;
    private Point _contentDragOrigin;

    /// <summary>图标被加入本 Fence（拖入到 ContentArea）。参数=本控件 + 被加入图标 FilePath。</summary>
    public event Action<FenceControl, string>? IconAdded;
    /// <summary>图标被移出本 Fence（拖出到画布空白）。参数=本控件 + 被移出图标 FilePath。</summary>
    public event Action<FenceControl, string>? IconRemoved;

    /// <summary>T7：本 Fence 的可持久化状态发生变化（标题/折叠/拖动坐标）时触发。
    /// 宿主订阅以触发防抖 Save。不在 IconAdded/Removed 触发（宿主归属回调里自己 Save）。</summary>
    public event Action? ConfigChanged;

    /// <summary>T7：共享 IconExtractor（宿主注入，跨 Fence 共用一份图标缓存）。</summary>
    public IconExtractor Icons
    {
        get => _icons;
        set => _icons = value ?? throw new ArgumentNullException(nameof(value));
    }

    public FenceControl()
    {
        InitializeComponent();
        // M5-UI：点盒子空白清除盒内选中（图标 panel 已 Handled，不会误触）。
        MouseLeftButtonDown += (_, _) => {
                ClearIconSelection();
                var host = Window.GetWindow(this) as IconLayerWindow;
                host?.ClearAllSelection();
            };
    }

    /// <summary>把 FenceConfig 映射到 UI（标题/折叠态/尺寸 W/H）。
    /// 坐标定位（Canvas.SetLeft/Top）留给宿主；本方法只设控件自身属性。</summary>
    public void Bind(FenceConfig config)
    {
        _config = config;
        _title = config.Title;
        _folded = config.Folded;
        TitleText.Text = _title;
        ContentArea.Visibility = _folded ? Visibility.Collapsed : Visibility.Visible;
        FoldButton.Content = _folded ? "▸" : "▾";
        // T7：应用尺寸 W/H（>0 才覆盖；保留 XAML MinWidth 兜底）。
        if (config.W > 0) Width = config.W;
        if (config.H > 0) Height = config.H;
        // 折叠态启动：应用展开高度后强制 Height=NaN（Auto），让 Border 缩到标题栏（与运行期折叠一致，
        // Fences 风格）。_config.H 已保存展开高度；展开态（_folded=false）保留上面 config.H 不覆盖。
        if (_folded) Height = double.NaN;
    }

    /// <summary>返回反映当前 UI 状态（拖动后坐标、折叠态、标题、尺寸）的 FenceConfig，供 T7 持久化。</summary>
    public FenceConfig BuildConfig()
    {
        var x = Canvas.GetLeft(this);
        var y = Canvas.GetTop(this);
        return _config with
        {
            Title = _title,
            Folded = _folded,
            X = double.IsNaN(x) ? _config.X : x,
            Y = double.IsNaN(y) ? _config.Y : y,
            // T7：读回控件实际尺寸（ActualWidth/Height 在 Measure 后有效；未挂画布时 NaN → 保留旧值）。
            W = !double.IsNaN(ActualWidth) && ActualWidth > 0 ? ActualWidth : _config.W,
            // 折叠时 Height=NaN（Auto）→ ActualHeight 是压缩后的标题栏小高度，不能写回（否则丢失展开尺寸）。
            // 改读 _config.H（折叠前已快照展开高度，见 FoldButton_Click）；展开时读 ActualHeight（resize 后更新）。
            H = _folded ? _config.H : (!double.IsNaN(ActualHeight) && ActualHeight > 0 ? ActualHeight : _config.H),
        };
    }

    /// <summary>T7 加载入口：批量渲染归属图标 + 维护 _config.IconFilePaths / _contentIcons，
    /// **不触发 IconAdded**（避免加载 N 个 Fence 各 M 图标时触发 N*M 次宿主重渲风暴）。
    /// 宿主在加载阶段自己初始化 _fencedPaths，不依赖事件回调。路径应已由宿主用 IconPathFilter 过滤容错。</summary>
    public void LoadIcons(IEnumerable<string> filePaths)
    {
        ClearIconSelection();
        foreach (var filePath in filePaths)
        {
            if (string.IsNullOrEmpty(filePath)) continue;
            if (_contentIcons.ContainsKey(filePath)) continue; // 去重
            var element = BuildContentIcon(filePath);
            _contentIcons[filePath] = element;
            ContentArea.Children.Add(element);
        }
        // 一次性同步 _config.IconFilePaths（与去重后的 _contentIcons 一致），不触发事件。
        _config = _config with { IconFilePaths = _contentIcons.Keys.ToList() };
    }

    // ---------- T3：归属图标增删 / 渲染 / 查询 ----------

    /// <summary>加入图标到本 Fence 内容区（拖入时由 ContentArea_Drop 调用，宿主也可直接调）。
    /// 去重；渲染小图标项 + 维护 _config.IconFilePaths + 触发 IconAdded。</summary>
    public void AddIcon(string filePath)
    {
        ClearIconSelection();
        if (string.IsNullOrEmpty(filePath)) return;
        if (filePath.StartsWith("::", StringComparison.Ordinal)) return; // shell 虚拟对象不入盒（无文件实体，重启无法恢复）
        if (_contentIcons.ContainsKey(filePath)) return; // 已归属，去重
        var element = BuildContentIcon(filePath);
        _contentIcons[filePath] = element;
        ContentArea.Children.Add(element);
        _config = _config with { IconFilePaths = _config.IconFilePaths.Append(filePath).ToList() };
        IconAdded?.Invoke(this, filePath);
    }

    /// <summary>从本 Fence 内容区移除图标（拖出到画布空白时由宿主调用）。
    /// 移除 UI + 维护 _config.IconFilePaths + 触发 IconRemoved。</summary>
    public void RemoveIcon(string filePath)
    {
        if (!_contentIcons.ContainsKey(filePath)) return; // 不存在不触发事件
        RemoveIconSilent(filePath);
        IconRemoved?.Invoke(this, filePath);
    }

    /// <summary>从本 Fence 内容区**静默**移除图标（不触发 IconRemoved）。
    /// 供宿主在跨 Fence 迁移时整理原 owner：拖入新 Fence 后，把 path 从其他仍含它的 Fence 移除。
    /// 关键：不触发 IconRemoved → 不会触发宿主 OnFenceIconRemoved → 不会把 path 从 _fencedPaths 移除
    ///（此时新 owner Fence 仍拥有该 path，_fencedPaths 必须保留它）。否则会错误丢失归属。</summary>
    public void RemoveIconSilent(string filePath)
    {
        ClearIconSelection();
        if (!_contentIcons.TryGetValue(filePath, out var element)) return;
        ContentArea.Children.Remove(element);
        _contentIcons.Remove(filePath);
        _config = _config with
        {
            IconFilePaths = _config.IconFilePaths
                .Where(p => !string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase))
                .ToList()
        };
    }

    /// <summary>本 Fence 是否已归属该 FilePath（供宿主在画布空白 Drop 时定位 owner）。</summary>
    public bool ContainsIcon(string filePath)
        => _contentIcons.ContainsKey(filePath);

    /// <summary>构造内容区小图标项：StackPanel(28x28 Image + 文件名 TextBlock)，支持左键拖出（移动阈值 → DoDragDrop）。
    /// M2 完善：原先仅 Image（文件名只能 hover 看），现加 TextBlock 显示名字，参考散落图标样式但更紧凑。
    /// 拖出逻辑挂在 StackPanel 上（与散落图标 panel 对称），保证拖出仍工作。</summary>
    private void FenceShell_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) =>
        FenceShell.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF));
    private void FenceShell_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
        FenceShell.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    private static int IconSizeOf(DependencyObject from)
    {
        var w = Window.GetWindow(from);
        return w is IconLayerWindow ilw ? Math.Max(20, ilw.IconSize - 14) : 28;
    }

    private FrameworkElement BuildContentIcon(string filePath)
    {
        var name = Path.GetFileName(filePath);
        // M6 美化：与散落图标同构——外层圆角 Border（hover 白底/选中蓝底），
        // 标签跟随窗口 LabelStyle（shadow=文字阴影 / pill=胶囊底），尺寸跟随 IconSize 档（盒内小一档）。
        int isz = IconSizeOf(this);
        var win = Window.GetWindow(this) as IconLayerWindow;
        bool shadowStyle = win?.LabelStyle != "pill";

        var img = new Image
        {
            Width = isz,
            Height = isz,
            Source = _icons.GetIcon(filePath),
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(img, System.Windows.Media.BitmapScalingMode.HighQuality);

        var labelText = new TextBlock
        {
            Text = name,
            FontSize = isz >= 44 ? 11 : 10,
            MaxWidth = isz + 18,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var labelHost = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        };
        if (shadowStyle)
            labelText.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.9 };
        else
            labelHost.Background = new SolidColorBrush(Color.FromArgb(102, 0, 0, 0)); // #66
        labelHost.Child = labelText;

        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(img);
        panel.Children.Add(labelHost);

        // 外层圆角壳（hover/选中态载体），替换旧的 StackPanel 直染背景
        var cell = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4, 2, 4, 2),
            Width = isz + 34,
            Margin = new Thickness(2),
            Background = Brushes.Transparent,
            ToolTip = name,
            Child = panel,
            Tag = filePath,
        };
        // M6：盒内图标右键 = 与散落图标同款可配置菜单（窗口 FillIconMenu 动态构建）。
        cell.ContextMenu = new ContextMenu();
        cell.ContextMenuOpening += (_, e) =>
        {
            if (Window.GetWindow(this) is IconLayerWindow ilw && cell.Tag is string fp)
                ilw.FillIconMenu(cell.ContextMenu, fp);
            else
                e.Handled = true;
        };
        cell.MouseEnter += (_, _) => { if (!ReferenceEquals(_selectedCell, cell)) cell.Background = HoverBrush; };
        cell.MouseLeave += (_, _) => { if (!ReferenceEquals(_selectedCell, cell)) cell.Background = Brushes.Transparent; };
        // 拖出逻辑：挂 panel（与散落图标对称），左键按下 arm；MouseMove 超阈值 → DoDragDrop（data=FilePath）。
        panel.MouseLeftButtonDown += (_, e) =>
        {
            // M5-UI 修：双击打开（原先 Handled=true 吞掉双击，盒内图标双击无反应）。
            if (e.ClickCount >= 2)
            {
                _contentDragArmed = false;
                _contentDragPath = null;
                Open(filePath);
                e.Handled = true;
                return;
            }
            _contentDragArmed = true;
            _contentDragPath = filePath;
            _contentDragOrigin = e.GetPosition(this);
            // 单选（跨盒/跨屏）：先清本屏全部选中（散落+所有收纳盒，含本盒旧选），
            // 再经 IPC 清其他屏（ClearAllSelection），最后高亮本图标。
            if (Window.GetWindow(this) is IconLayerWindow ilw)
            {
                ilw.ClearLocalSelection();
                ilw.ClearAllSelection();
            }
            SelectIcon(cell, panel);
            e.Handled = true;
        };
        panel.MouseMove += (_, e) =>
        {
            if (!_contentDragArmed || _contentDragPath is null) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _contentDragOrigin.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(pos.Y - _contentDragOrigin.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                var path = _contentDragPath;
                _contentDragArmed = false;
                _contentDragPath = null;
                // M2 真机修复：DataObject 同时含 FileDrop（explorer/文件夹认 + 系统拖拽反馈）+ Text
                // （兼容 Fence_Drop / IconCanvas_Drop 按 Text 读）。与散落图标 Loose_PreviewMouseMove 对称。
                var data = new DataObject();
                data.SetData(DataFormats.FileDrop, new[] { path });
                data.SetData(DataFormats.Text, path);
                DragDrop.DoDragDrop(panel, data, DragDropEffects.Move);
            }
        };
        panel.MouseLeftButtonUp += (_, _) =>
        {
            _contentDragArmed = false;
            _contentDragPath = null;
        };
        return cell;
    }

    // ---------- M5-UI：盒内图标选中 ----------

    // M6 美化：hover/选中画在外层圆角壳（cell）；_selectedIconPanel 仍持 panel 做身份比较。
    private static readonly Brush HoverBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    private Border? _selectedCell;

    private void SelectIcon(Border cell, StackPanel panel)
    {
        if (_selectedCell is not null && !ReferenceEquals(_selectedCell, cell))
            _selectedCell.Background = Brushes.Transparent;
        _selectedCell = cell;
        cell.Background = SelectedBrush;
    }

    /// <summary>清除盒内图标选中态（点盒子空白/图标增删时调）。</summary>
    public void ClearIconSelection()
    {
        if (_selectedCell is not null) _selectedCell.Background = Brushes.Transparent;
        _selectedCell = null;
    }

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "", // .lnk 相对路径修复（同 IconLayerWindow.Open）
            });
        }
        catch { /* 与 IconLayerWindow.Open 同：失败静默（M1 遗留语义） */ }
    }

    // ---------- T3：拖入接收（UserControl 根 Drop） ----------
    // M2 真机修复 Bug 1：Drop 处理从 ContentArea 提升到 UserControl 根（Fence_DragOver/Fence_Drop）。
    // 原先仅 ContentArea（WrapPanel）接收：空盒 MinHeight=20 + 折叠态 Collapsed，命中 Border/HeaderBar 时
    // 事件冒泡到 IconCanvas_Drop（无 owner → no-op）。根级 Drop 覆盖整个盒子可视区域，标题栏/边框/空白均可接收。

    private void Fence_DragOver(object sender, DragEventArgs e)
    {
        // 只认 Text（app 图标拖入 Fence 归属）。FileDrop（外部文件）不 Handled → 冒泡到 IconCanvas_DragOver
        // （IconCanvas 把外部文件移到桌面散落区）。避免 Fence 覆盖区域对外部文件显示"禁止"光标的体验割裂。
        if (!e.Data.GetDataPresent(DataFormats.Text)) return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void Fence_Drop(object sender, DragEventArgs e)
    {
        // 非 Text（外部文件 FileDrop）不 Handled → 冒泡到 IconCanvas_Drop（移到桌面散落区，不加入本 Fence）。
        if (!e.Data.GetDataPresent(DataFormats.Text)) return;
        var path = (string)e.Data.GetData(DataFormats.Text);
        AddIcon(path); // 去重 + 渲染 + 触发 IconAdded（宿主据此更新散落区）
        e.Handled = true; // 阻止冒泡到 IconCanvas，否则宿主会误当「拖出到空白」处理
    }

    // ---------- 顶栏：拖动 + 双击进入标题编辑 ----------

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isEditing)
        {
            return;
        }
        if (e.ClickCount >= 2)
        {
            BeginTitleEdit();
            e.Handled = true;
            return;
        }
        if (Parent is not Canvas canvas)
        {
            // 未挂到画布（T2 spike 独立测试场景）：拖动无意义，静默忽略，不崩。
            return;
        }
        _isDragging = true;
        _dragOrigin = e.GetPosition(canvas);
        _startLeft = Canvas.GetLeft(this);
        _startTop = Canvas.GetTop(this);
        if (double.IsNaN(_startLeft)) _startLeft = _config.X;
        if (double.IsNaN(_startTop)) _startTop = _config.Y;
        HeaderBar.CaptureMouse();
        e.Handled = true;
    }

    private void HeaderBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || Parent is not Canvas canvas)
        {
            return;
        }
        // M3-T5：拖出本窗口边界 → 转 OLE 跨屏拖拽（目标屏 IconCanvas 接收）。
        // CaptureMouse 模式下先释放捕获再进 DoDragDrop 模态循环；拖拽结果由目标窗 Drop 处理。
        var win = Window.GetWindow(this) as IconLayerWindow;
        if (win is not null)
        {
            var winPos = e.GetPosition(win);
            if (winPos.X < 0 || winPos.Y < 0 || winPos.X > win.ActualWidth || winPos.Y > win.ActualHeight)
            {
                _isDragging = false;
                HeaderBar.ReleaseMouseCapture();
                win.BeginFenceCrossScreenDrag(this);
                return;
            }
        }
        var pos = e.GetPosition(canvas);
        Canvas.SetLeft(this, _startLeft + (pos.X - _dragOrigin.X));
        Canvas.SetTop(this, _startTop + (pos.Y - _dragOrigin.Y));
    }

    private void HeaderBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }
        _isDragging = false;
        HeaderBar.ReleaseMouseCapture();
        e.Handled = true;
        // T7：拖动结束 → 坐标变化 → 通知宿主持久化。
        ConfigChanged?.Invoke();
    }

    // ---------- 折叠 ----------

    private void FoldButton_Click(object sender, RoutedEventArgs e)
    {
        // 折叠前快照当前展开高度到 _config.H（resize 后 ActualHeight 可能 > 旧 _config.H）。
        // 供展开恢复（Height=_config.H）+ BuildConfig 折叠态读 _config.H 持久化，
        // 避免 resize→折叠→展开 丢高度（_config.H 仅 Bind 时从磁盘读入，resize 不写回内存 _config）。
        if (!_folded && !double.IsNaN(ActualHeight) && ActualHeight > 0)
            _config = _config with { H = ActualHeight };
        _folded = !_folded;
        ContentArea.Visibility = _folded ? Visibility.Collapsed : Visibility.Visible;
        FoldButton.Content = _folded ? "▸" : "▾";
        // 折叠：Height=NaN（Auto）让 Border 缩到标题栏（Fences 风格，只缩高宽不变）；
        // 展开：恢复 _config.H（折叠前已快照，含 resize 后的最新展开高度）。
        Height = _folded ? double.NaN : _config.H;
        // T7：折叠态变化 → 通知宿主持久化。
        ConfigChanged?.Invoke();
    }

    // ---------- 标题编辑（双击进入；回车/失焦确认；Esc 取消） ----------

    /// <summary>触发标题编辑（供宿主右键菜单「重命名」调用，与双击标题同一编辑流程）。</summary>
    public void BeginRename() => BeginTitleEdit();

    private void BeginTitleEdit()
    {
        if (_isEditing) return; // 守卫：右键「重命名」/双击标题双重触发不重复进入（避免 BeginInput 重入）
        _isEditing = true;
        // M2 真机修复 Bug 2：宿主窗口 NOACTIVATE 时 TextBox 无法接收键盘输入，编辑前临时前台化。
        // 必须在 TitleEdit.Focus() 前调，让 app 先获得前台焦点，TextBox 才能拿到键盘焦点。
        if (Window.GetWindow(this) is IInteractiveHost host) host.BeginInput();
        TitleEdit.Text = _title;
        TitleText.Visibility = Visibility.Collapsed;
        TitleEdit.Visibility = Visibility.Visible;
        TitleEdit.Focus();
        TitleEdit.SelectAll();
    }

    private void TitleEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitTitleEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelTitleEdit();
            e.Handled = true;
        }
    }

    private void TitleEdit_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitTitleEdit();
    }

    private void CommitTitleEdit()
    {
        if (!_isEditing)
        {
            return;
        }
        _title = TitleEdit.Text;
        TitleText.Text = _title;
        EndTitleEdit();
        // T7：标题确认变化 → 通知宿主持久化。（LostFocus 也会走这里；_isEditing 守卫保证只在真编辑后触发一次。）
        ConfigChanged?.Invoke();
    }

    private void CancelTitleEdit()
    {
        if (!_isEditing)
        {
            return;
        }
        EndTitleEdit();
    }

    private void EndTitleEdit()
    {
        _isEditing = false;
        TitleEdit.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        // M2 真机修复 Bug 2：编辑结束恢复 NOACTIVATE + 回桌面层 Z-order（与 BeginInput 严格配对）。
        // Commit/Cancel/LostFocus 三条出口都经 EndTitleEdit，确保 EndInput 必触发。
        if (Window.GetWindow(this) is IInteractiveHost host) host.EndInput();
    }

    // ---------- 边缘 resize（替代右下角 Thumb） ----------
    // 鼠标移到 Border 右边/下边/右下角边缘 → 对应 resize 光标（SizeWE/SizeNS/SizeNWSE）；
    // 左键按下 → 记起点 + 初始 Width/Height + CaptureMouse；拖动 → 更新 W/H（最小值兜底，Math.Max）；
    // 抬起 → ReleaseMouseCapture + 触发 ConfigChanged 持久化。
    // 持久化闭环同旧 Thumb：resize → ActualWidth/Height 变 → Up 触发 ConfigChanged → 宿主 SaveFencesDebounced
    // → BuildConfig 读 ActualWidth/Height 写回 W/H（已在 BuildConfig 就绪）。
    //
    // 不干扰其他交互（关键）：
    // - HeaderBar 拖盒子：HeaderBar MouseLeftButtonDown 末尾 e.Handled=true → 不冒泡到 UserControl 根，
    //   且 HeaderBar 在顶栏（非右/下边缘），边缘检测亦不会命中。
    // - 内容图标拖出：BuildContentIcon 的 MouseLeftButtonDown 设 e.Handled=true → 不冒泡到根；
    //   且图标在内容区中部，不在边缘 6px 内。
    // - FoldButton：Button 自身处理鼠标事件并标记 handled，且位于顶栏（非边缘）。
    // 折叠态禁用（缩到标题栏不需 resize）：_folded 时边缘光标检测 return（保持默认 Arrow）+ Down 直接 return。

    private const double ResizeEdgeThreshold = 6;   // 距边缘 <6px 判为 resize 边
    private const double ResizeMinWidth = 120;
    private const double ResizeMinHeight = 80;

    private bool _isResizing;
    private bool _resizeOnRight;        // 抓的是右边（横向 resize）
    private bool _resizeOnBottom;       // 抓的是下边（纵向 resize）
    private Point _resizeOrigin;        // 按下时鼠标相对本控件的位置
    private double _resizeStartWidth;   // 按下时控件 Width
    private double _resizeStartHeight;  // 按下时控件 Height（NaN→取 ActualHeight）

    private void Fence_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(this);

        // resize 进行中：按起点 + 增量更新 W/H（仅更新被抓的轴，最小值兜底）。
        if (_isResizing)
        {
            if (_resizeOnRight)
                Width = Math.Max(ResizeMinWidth, _resizeStartWidth + (pos.X - _resizeOrigin.X));
            if (_resizeOnBottom)
                Height = Math.Max(ResizeMinHeight, _resizeStartHeight + (pos.Y - _resizeOrigin.Y));
            return;
        }

        // 折叠态：禁用边缘 resize，光标保持默认（不设边缘光标）。
        if (_folded) return;

        // 非拖动：据鼠标距右/下边缘位置设 resize 光标。
        var (onRight, onBottom) = HitResizeEdge(pos);
        if (onRight && onBottom) Cursor = Cursors.SizeNWSE;
        else if (onRight) Cursor = Cursors.SizeWE;
        else if (onBottom) Cursor = Cursors.SizeNS;
        else Cursor = Cursors.Arrow;
    }

    private void Fence_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 折叠态不启动 resize（缩到标题栏，盒子高度已为 Auto，不提供 resize）。
        if (_folded) return;

        var pos = e.GetPosition(this);
        var (onRight, onBottom) = HitResizeEdge(pos);
        if (!onRight && !onBottom) return;   // 非边缘：留给 HeaderBar/内容图标/空白（不拦截）

        _isResizing = true;
        _resizeOnRight = onRight;
        _resizeOnBottom = onBottom;
        _resizeOrigin = pos;
        _resizeStartWidth = Width;
        _resizeStartHeight = double.IsNaN(Height) ? ActualHeight : Height;
        CaptureMouse();   // 捕获 → 拖出控件外仍收 Move/Up，resize 体验连贯
        e.Handled = true; // 标记 handled，避免同时触发 HeaderBar/拖盒子等
    }

    private void Fence_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isResizing) return;
        _isResizing = false;
        _resizeOnRight = _resizeOnBottom = false;
        ReleaseMouseCapture();
        e.Handled = true;
        // resize 结束一次触发持久化（vs 拖动中每次触发 → 频繁 SaveFencesDebounced，虽防抖兜底但更脏）。
        ConfigChanged?.Invoke();
    }

    /// <summary>判断鼠标是否命中边缘 resize 区：距右 <阈值 或 距下 <阈值。
    /// 用 ActualWidth/Height（渲染尺寸）作边缘基准，与可视边缘一致。</summary>
    private (bool onRight, bool onBottom) HitResizeEdge(Point pos)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return (false, false);
        bool onRight = ActualWidth - pos.X < ResizeEdgeThreshold && pos.X >= 0;
        bool onBottom = ActualHeight - pos.Y < ResizeEdgeThreshold && pos.Y >= 0;
        return (onRight, onBottom);
    }
}
