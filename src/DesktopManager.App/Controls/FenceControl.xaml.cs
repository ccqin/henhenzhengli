using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopManager.Core.Models;

namespace DesktopManager.App.Controls;

/// <summary>
/// 收纳盒控件（Fences 风格）：半透明、可整体拖动、可折叠、标题可编辑的盒子。
/// T2 只实现盒子本身；图标拖入/拖出（T3）、右键菜单（T6）、挂载到 IconLayerWindow（T7）不在本任务。
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

    public FenceControl()
    {
        InitializeComponent();
    }

    /// <summary>把 FenceConfig 映射到 UI（标题/折叠态）。坐标/尺寸定位留给 T7 宿主（Canvas.SetLeft/Top）。</summary>
    public void Bind(FenceConfig config)
    {
        _config = config;
        _title = config.Title;
        _folded = config.Folded;
        TitleText.Text = _title;
        ContentArea.Visibility = _folded ? Visibility.Collapsed : Visibility.Visible;
        FoldButton.Content = _folded ? "▸" : "▾";
    }

    /// <summary>返回反映当前 UI 状态（拖动后坐标、折叠态、标题）的 FenceConfig，供 T7 持久化。</summary>
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
        };
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
    }

    // ---------- 折叠 ----------

    private void FoldButton_Click(object sender, RoutedEventArgs e)
    {
        _folded = !_folded;
        ContentArea.Visibility = _folded ? Visibility.Collapsed : Visibility.Visible;
        FoldButton.Content = _folded ? "▸" : "▾";
    }

    // ---------- 标题编辑（双击进入；回车/失焦确认；Esc 取消） ----------

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
