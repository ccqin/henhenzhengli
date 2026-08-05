using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DesktopManager.Core.Models;
using DesktopManager.Core.Services;
using DesktopManager.Native;

namespace DesktopManager.App.Windows;

/// <summary>M5-T2/T5：设置窗口（单实例，托盘入口）。
/// 左：屏幕排列预览——矩形可拖（ArrangementPlanner 吸附/连通），「应用排列」把新拓扑写进 Windows
/// （ChangeDisplaySettingsEx；成功后 WM_DISPLAYCHANGE 触发 M3 重建）；右：显示组管理。</summary>
public partial class SettingsWindow : Window
{
    private readonly MultiMonitorHost _host;
    private List<DisplayGroup> _groups;
    private List<MonitorInfo> _monitors = new();
    private int _selectedGroup = -1;
    private bool _suppressEvents;

    // 排列预览状态（虚拟屏坐标）+ 画布映射
    private readonly Dictionary<string, IntRect> _preview = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Rectangle Rect, TextBlock Label)> _previewEls = new();
    private double _scale = 1, _offX, _offY, _virtL, _virtT;

    // 拖拽
    private string? _dragId;
    private Point _dragOrigin;
    private IntRect _dragStart;

    private const double PreviewW = 560;
    private const double PreviewH = 360;

    public SettingsWindow(MultiMonitorHost host)
    {
        _host = host;
        InitializeComponent();
        _groups = host.Groups.ToList();
        RefreshMonitors();
        RefreshGroupsUI();
    }

    // ---------- 排列预览 + 拖拽 ----------

    private void RefreshMonitors()
    {
        _monitors = MonitorEnumerator.Enumerate().ToList();
        _preview.Clear();
        foreach (var m in _monitors)
            _preview[m.PersistentId] = new IntRect(m.X, m.Y, m.X + m.Width, m.Y + m.Height);
        DrawPreview();
        ApplyArrangementButton.IsEnabled = false;
    }

    /// <summary>重建预览元素（仅结构变化时：刷新/重置）。拖拽中只用 UpdatePreviewPositions——
    /// 真机栈溢出根因：拖拽中重建元素 + CaptureMouse 新矩形 → MouseMove 内同步触发新元素
    /// MouseMove → 无限递归（0xc00000fd，探针 depth=1000+）。</summary>
    private void DrawPreview()
    {
        PreviewCanvas.Children.Clear();
        _previewEls.Clear();
        if (_preview.Count == 0) return;

        foreach (var m in _monitors)
        {
            if (!_preview.ContainsKey(m.PersistentId)) continue;
            var rect = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromRgb(0x2E, 0x3A, 0x5E)),
                Stroke = new SolidColorBrush(m.IsPrimary ? Colors.Gold : Colors.SteelBlue),
                StrokeThickness = 2,
                Tag = m.PersistentId,
                Cursor = Cursors.SizeAll
            };
            rect.MouseLeftButtonDown += Rect_MouseDown;
            rect.MouseMove += Rect_MouseMove;
            rect.MouseLeftButtonUp += Rect_MouseUp;
            PreviewCanvas.Children.Add(rect);

            var label = new TextBlock
            {
                Foreground = new SolidColorBrush(Colors.WhiteSmoke),
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false
            };
            PreviewCanvas.Children.Add(label);
            _previewEls[m.PersistentId] = (rect, label);
        }
        UpdatePreviewPositions();
    }

    /// <summary>按当前 _preview 重算映射并移动既有元素（拖拽中高频调用，零重建、零捕获切换）。</summary>
    private void UpdatePreviewPositions()
    {
        if (_preview.Count == 0) return;
        var rects = _preview.Values.ToList();
        _virtL = rects.Min(r => r.Left);
        _virtT = rects.Min(r => r.Top);
        double virtW = rects.Max(r => r.Right) - _virtL;
        double virtH = rects.Max(r => r.Bottom) - _virtT;
        _scale = Math.Min((PreviewW - 16) / virtW, (PreviewH - 16) / virtH);
        _offX = (PreviewW - virtW * _scale) / 2;
        _offY = (PreviewH - virtH * _scale) / 2;

        foreach (var m in _monitors)
        {
            if (!_preview.TryGetValue(m.PersistentId, out var r) ||
                !_previewEls.TryGetValue(m.PersistentId, out var els)) continue;
            els.Rect.Width = r.Width * _scale;
            els.Rect.Height = r.Height * _scale;
            Canvas.SetLeft(els.Rect, (r.Left - _virtL) * _scale + _offX);
            Canvas.SetTop(els.Rect, (r.Top - _virtT) * _scale + _offY);
            els.Label.Text = $"{ShortName(m.PersistentId)}{(m.IsPrimary ? " ★" : "")}\n{r.Width}x{r.Height}";
            Canvas.SetLeft(els.Label, (r.Left - _virtL) * _scale + _offX + 6);
            Canvas.SetTop(els.Label, (r.Top - _virtT) * _scale + _offY + 6);
        }
    }

    private void Rect_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Rectangle rect || rect.Tag is not string id) return;
        _dragId = id;
        _dragOrigin = e.GetPosition(PreviewCanvas);
        _dragStart = _preview[id];
        rect.CaptureMouse();
        e.Handled = true;
    }

    private void Rect_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragId is null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(PreviewCanvas);
        int dx = (int)Math.Round((pos.X - _dragOrigin.X) / _scale);
        int dy = (int)Math.Round((pos.Y - _dragOrigin.Y) / _scale);
        var dragged = _dragStart.Shift(dx, dy);
        var others = _preview.Where(kv => kv.Key != _dragId).Select(kv => kv.Value).ToList();
        _preview[_dragId] = ArrangementPlanner.Plan(dragged, others);
        // 元素稳定（DrawPreview 仅结构变化时调），拖拽中只移位置：
        // 重建元素+CaptureMouse 新矩形会在 MouseMove 内同步触发新元素的 MouseMove → 递归栈溢出（真机 0xc00000fd，探针 depth=1000+）。
        UpdatePreviewPositions();
    }

    private void Rect_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragId is null) return;
        (sender as Rectangle)?.ReleaseMouseCapture();
        _dragId = null;
        // 与真实拓扑有差异 → 启用应用
        bool changed = _monitors.Any(m =>
            _preview.TryGetValue(m.PersistentId, out var r) &&
            (r.Left != m.X || r.Top != m.Y));
        ApplyArrangementButton.IsEnabled = changed;
    }

    private void ApplyArrangement_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this,
            "把拖拽后的排列应用到 Windows 真实显示拓扑？\n屏幕会黑一下，鼠标跨屏方向随之改变。",
            "应用排列", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;

        var positions = new Dictionary<string, (int, int, int, int)>();
        foreach (var m in _monitors)
        {
            if (_preview.TryGetValue(m.PersistentId, out var rect))
                positions[m.DeviceName] = (rect.Left, rect.Top, rect.Width, rect.Height);
        }
        var (ok, err) = DisplayTopologyApplier.Apply(positions);
        if (!ok)
        {
            MessageBox.Show(this,
                $"应用失败：{err}\n\n本机的显示栈（DWM 虚拟显示模式）拒绝第三方程序修改显示拓扑（提权亦无效）。\n" +
                "请改用 Windows 设置 → 屏幕 调整排列；拖拽预览仍可用于规划布局。",
                "应用排列", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ApplyArrangementButton.IsEnabled = false;
        // WM_DISPLAYCHANGE → M3 重建自动跟随；刷新预览到真实拓扑。
        RefreshMonitors();
    }

    private void ResetPreview_Click(object sender, RoutedEventArgs e) => RefreshMonitors();
    private void RefreshMonitors_Click(object sender, RoutedEventArgs e) => RefreshMonitors();

    private static string ShortName(string persistentId)
    {
        var i = persistentId.LastIndexOf('#');
        return i >= 0 ? persistentId[(i + 1)..] : persistentId;
    }

    // ---------- 显示组管理（M5-T2） ----------

    private void RefreshGroupsUI()
    {
        _suppressEvents = true;
        GroupList.ItemsSource = _groups
            .Select(g => $"{g.Name}（{g.MonitorIds.Count} 屏{(string.IsNullOrWhiteSpace(g.WallpaperPath) ? "" : "，有壁纸")}）")
            .ToList();
        if (_selectedGroup >= _groups.Count) _selectedGroup = _groups.Count - 1;
        GroupList.SelectedIndex = _selectedGroup;
        _suppressEvents = false;
        RefreshMemberUI();
    }

    private DisplayGroup? SelectedGroup =>
        _selectedGroup >= 0 && _selectedGroup < _groups.Count ? _groups[_selectedGroup] : null;

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _selectedGroup = GroupList.SelectedIndex;
        RefreshMemberUI();
    }

    private void RefreshMemberUI()
    {
        MemberChecks.Children.Clear();
        GroupWallpaperText.Text = string.IsNullOrWhiteSpace(SelectedGroup?.WallpaperPath)
            ? "（无）" : SelectedGroup!.WallpaperPath;
        var g = SelectedGroup;
        if (g is null) return;

        foreach (var m in _monitors)
        {
            var box = new CheckBox
            {
                Content = $"{ShortName(m.PersistentId)}（{m.Width}x{m.Height}）",
                IsChecked = g.MonitorIds.Contains(m.PersistentId),
                Tag = m.PersistentId,
                Foreground = new SolidColorBrush(Colors.WhiteSmoke),
                Margin = new Thickness(0, 2, 0, 2)
            };
            box.Checked += MemberToggled;
            box.Unchecked += MemberToggled;
            MemberChecks.Children.Add(box);
        }
    }

    private void MemberToggled(object sender, RoutedEventArgs e)
    {
        var g = SelectedGroup;
        if (g is null || sender is not CheckBox box || box.Tag is not string monId) return;
        var ids = g.MonitorIds.ToList();
        if (box.IsChecked == true) { if (!ids.Contains(monId)) ids.Add(monId); }
        else ids.Remove(monId);
        ReplaceSelected(g with { MonitorIds = ids });
    }

    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        _groups.Add(new DisplayGroup
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = $"组 {_groups.Count + 1}",
            MonitorIds = _monitors.Select(m => m.PersistentId).ToList()
        });
        _selectedGroup = _groups.Count - 1;
        Commit();
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        var g = SelectedGroup;
        if (g is null) return;
        _groups.Remove(g);
        Commit();
    }

    private void SetGroupWallpaper_Click(object sender, RoutedEventArgs e)
    {
        var g = SelectedGroup;
        if (g is null) { MessageBox.Show(this, "先选择或新建一个组", "设置组壁纸"); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择组壁纸（图片/视频/GIF，跨屏拼接）",
            Filter = "壁纸|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.mp4;*.wmv;*.avi;*.m4v|所有文件|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        ReplaceSelected(g with
        {
            WallpaperKind = WallpaperConfig.DetectKind(dlg.FileName),
            WallpaperPath = dlg.FileName
        });
    }

    private void ClearGroupWallpaper_Click(object sender, RoutedEventArgs e)
    {
        var g = SelectedGroup;
        if (g is null) return;
        ReplaceSelected(g with { WallpaperPath = "" });
    }

    private void ReplaceSelected(DisplayGroup updated)
    {
        if (_selectedGroup < 0 || _selectedGroup >= _groups.Count) return;
        _groups[_selectedGroup] = updated;
        Commit();
    }

    private void Commit()
    {
        _host.SetDisplayGroups(_groups);
        RefreshGroupsUI();
    }
}
