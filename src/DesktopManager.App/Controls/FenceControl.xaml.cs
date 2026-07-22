using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopManager.App.Services;
using DesktopManager.Core.Models;

namespace DesktopManager.App.Controls;

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
        // 折叠交互：只切 ContentArea.Visibility，不动控件 Height —— 盒子高度恒定，内容隐藏其内，
        // 行为可预测（Fences 风格），避免折叠后 BuildConfig 读到压缩的 ActualHeight 写回丢失展开尺寸。
        if (config.W > 0) Width = config.W;
        if (config.H > 0) Height = config.H;
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
            H = !double.IsNaN(ActualHeight) && ActualHeight > 0 ? ActualHeight : _config.H,
        };
    }

    /// <summary>T7 加载入口：批量渲染归属图标 + 维护 _config.IconFilePaths / _contentIcons，
    /// **不触发 IconAdded**（避免加载 N 个 Fence 各 M 图标时触发 N*M 次宿主重渲风暴）。
    /// 宿主在加载阶段自己初始化 _fencedPaths，不依赖事件回调。路径应已由宿主用 IconPathFilter 过滤容错。</summary>
    public void LoadIcons(IEnumerable<string> filePaths)
    {
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
        if (string.IsNullOrEmpty(filePath)) return;
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

    /// <summary>构造内容区小图标项：28x28 Image + 文件名 ToolTip，支持左键拖出（移动阈值 → DoDragDrop）。</summary>
    private FrameworkElement BuildContentIcon(string filePath)
    {
        var img = new Image
        {
            Width = 28,
            Height = 28,
            Source = _icons.GetIcon(filePath),
            Stretch = Stretch.Uniform,
            ToolTip = Path.GetFileName(filePath),
            VerticalAlignment = VerticalAlignment.Top
        };
        // 左键按下 arm 拖拽候选；MouseMove 超阈值 → DoDragDrop（data=FilePath，与散落区一致）。
        img.MouseLeftButtonDown += (_, e) =>
        {
            _contentDragArmed = true;
            _contentDragPath = filePath;
            _contentDragOrigin = e.GetPosition(this);
            e.Handled = true;
        };
        img.MouseMove += (_, e) =>
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
                DragDrop.DoDragDrop(img, path, DragDropEffects.Move);
            }
        };
        img.MouseLeftButtonUp += (_, _) =>
        {
            _contentDragArmed = false;
            _contentDragPath = null;
        };
        return img;
    }

    // ---------- T3：拖入接收（ContentArea Drop） ----------

    private void ContentArea_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void ContentArea_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.Text)) { e.Handled = true; return; }
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
        _folded = !_folded;
        ContentArea.Visibility = _folded ? Visibility.Collapsed : Visibility.Visible;
        FoldButton.Content = _folded ? "▸" : "▾";
        // T7：折叠态变化 → 通知宿主持久化。
        ConfigChanged?.Invoke();
    }

    // ---------- 标题编辑（双击进入；回车/失焦确认；Esc 取消） ----------

    /// <summary>触发标题编辑（供宿主右键菜单「重命名」调用，与双击标题同一编辑流程）。</summary>
    public void BeginRename() => BeginTitleEdit();

    private void BeginTitleEdit()
    {
        _isEditing = true;
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
    }
}
