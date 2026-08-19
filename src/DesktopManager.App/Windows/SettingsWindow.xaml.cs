using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DesktopManager.Core.Models;
using DesktopManager.Native;

namespace DesktopManager.App.Windows;

/// <summary>M5-T2（M5-T5 修订）：设置窗口（单实例，托盘入口）。
/// 左：屏幕排列只读预览（等比矩形；拖拽改拓扑功能因本机显示栈拒绝第三方变更而移除）。
/// 右：显示组管理（组内屏共享壁纸）+ 每屏独立壁纸设置（原桌面右键入口收归此处）。</summary>

public class MonitorVm
{
    public string PersistentId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Resolution { get; set; } = "";
    public bool IsGroup { get; set; }
    public string GroupName { get; set; } = "";
    public string StatusText { get; set; } = "";
    public string StatusIcon { get; set; } = "";
}

public partial class SettingsWindow : Window
{
    // Win32 常量和 P/Invoke
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;
    private const int WM_SHOWWINDOW = 0x0018;

    
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly MultiMonitorHost _host;
    private List<DisplayGroup> _groups;
    private List<MonitorInfo> _monitors = new();
    private int _selectedGroup = -1;
    private string? _selectedMonitor;
    private bool _suppressEvents;

    // 排列预览（只读）画布映射
    private readonly Dictionary<string, IntRect> _preview = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Rectangle Rect, TextBlock Label)> _previewEls = new();
    private double _scale = 1, _offX, _offY, _virtL, _virtT;

    private const double PreviewW = 560;
    private const double PreviewH = 360;

    public SettingsWindow(MultiMonitorHost host)
    {
        _host = host;
        InitializeComponent();
        _groups = host.Groups.ToList();
        PanelAppearance.Host = host;
        PanelMenu.Host = host;
        var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        VersionText.Text = v is null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
        RefreshMonitors();
        RefreshGroupsUI();
        RefreshMonitorList();
        
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            exStyle |= WS_EX_TOOLWINDOW;
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        };
    }

    // ---------- title bar / nav ----------

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void RefreshMonitors_Click(object sender, RoutedEventArgs e)
    {
        RefreshMonitors();
        RefreshMonitorList();
        
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            exStyle |= WS_EX_TOOLWINDOW;
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
            
            // 添加消息钩子，拦截 Win+D
        };
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelArrange is null || PanelGroups is null || PanelMonitors is null) return;
        PanelArrange.Visibility = ReferenceEquals(sender, NavArrange) ? Visibility.Visible : Visibility.Collapsed;
        PanelGroups.Visibility = ReferenceEquals(sender, NavGroups) ? Visibility.Visible : Visibility.Collapsed;
        PanelMonitors.Visibility = ReferenceEquals(sender, NavMonitors) ? Visibility.Visible : Visibility.Collapsed;
        bool logPage = ReferenceEquals(sender, NavLogs);
        PanelLogs.Visibility = logPage ? Visibility.Visible : Visibility.Collapsed;
        if (logPage) PanelLogs.RefreshLogs();  // 进入页时拉最新
        PanelAppearance.Visibility = ReferenceEquals(sender, NavAppearance) ? Visibility.Visible : Visibility.Collapsed;
        if (ReferenceEquals(sender, NavAppearance)) PanelAppearance.LoadAppearanceUI();
        PanelMenu.Visibility = ReferenceEquals(sender, NavMenu) ? Visibility.Visible : Visibility.Collapsed;
        if (ReferenceEquals(sender, NavMenu)) PanelMenu.LoadMenuUI();
    }

    // ---------- 排列预览（只读） ----------

    private void RefreshMonitors()
    {
        _monitors = MonitorEnumerator.Enumerate().ToList();
        _preview.Clear();
        foreach (var m in _monitors)
            _preview[m.PersistentId] = new IntRect(m.X, m.Y, m.X + m.Width, m.Y + m.Height);
        DrawPreview();
    }

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
                Tag = m.PersistentId
            };
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

            var cfg = _host.GetEffectiveWallpaper(m.PersistentId);
            Brush? fill = null;
            string wallTag = "";

            if (cfg != null)
            {
                var group = _host.Groups.FirstOrDefault(g => 
                    !string.IsNullOrWhiteSpace(g.WallpaperPath) && g.MonitorIds.Contains(m.PersistentId));
                
                if (group != null && File.Exists(group.WallpaperPath))
                {
                    var groupMonitors = _monitors.Where(gm => group.MonitorIds.Contains(gm.PersistentId)).ToList();
                    double gMinX = groupMonitors.Min(gm => gm.X);
                    double gMinY = groupMonitors.Min(gm => gm.Y);
                    double gMaxX = groupMonitors.Max(gm => gm.X + gm.Width);
                    double gMaxY = groupMonitors.Max(gm => gm.Y + gm.Height);
                    double gW = gMaxX - gMinX;
                    double gH = gMaxY - gMinY;

                    if (gW > 0 && gH > 0)
                    {
                        try
                        {
                            var bmp = Services.ImageLoader.Load(group.WallpaperPath, 800) as BitmapImage;

                            double relX = (m.X - gMinX) / gW;
                            double relY = (m.Y - gMinY) / gH;
                            double relW = m.Width / gW;
                            double relH = m.Height / gH;

                            fill = new ImageBrush(bmp)
                            {
                                Stretch = Stretch.Fill,
                                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                                Viewbox = new Rect(relX, relY, relW, relH)
                            };
                            wallTag = "\n\u25cf \u7ec4\u58c1\u7eb8";
                        }
                        catch { }
                    }
                }
                
                if (fill == null)
                {
                    fill = ThumbnailBrush(cfg!);
                    wallTag = cfg.Kind == WallpaperKind.Video ? "\n\u25b6 \u89c6\u9891" : "\n\u25cf \u72ec\u7acb\u58c1\u7eb8";
                }
            }
            
            els.Rect.Fill = fill ?? new SolidColorBrush(Color.FromRgb(0x2E, 0x3A, 0x5E));
            els.Label.Text = $"{ShortName(m.PersistentId)}{(m.IsPrimary ? " \u2605" : "")}\n{r.Width}x{r.Height}{wallTag}";
        }
    }

    /// <summary>壁纸缩略图画刷：图片/GIF 首帧解码 300px 宽；视频/无/失败返回 null（调用方占位）。</summary>
    private static Brush? ThumbnailBrush(WallpaperConfig? cfg)
    {
        if (cfg is null || cfg.Kind == WallpaperKind.Video || !File.Exists(cfg.Path)) return null;
        var bmp = Services.ImageLoader.Load(cfg.Path, 300);  // 非常规 JPEG 走 GDI+ 兜底（与壁纸渲染同语义）
        return bmp is null ? null : new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
    }

    private static string ShortName(string persistentId)
    {
        var i = persistentId.LastIndexOf('#');
        return i >= 0 ? persistentId[(i + 1)..] : persistentId;
    }

    // ---------- 每屏独立壁纸（原桌面右键入口收归此处） ----------

    private void RefreshMonitorList()
    {
        _suppressEvents = true;
        var vms = _monitors.Select(m => {
            var cfg = _host.GetEffectiveWallpaper(m.PersistentId);
            var vm = new MonitorVm {
                PersistentId = m.PersistentId,
                Name = ShortName(m.PersistentId),
                Resolution = m.Width + "x" + m.Height
            };
            if (cfg != null) {
                var g = _host.Groups.FirstOrDefault(gx => !string.IsNullOrWhiteSpace(gx.WallpaperPath) && gx.MonitorIds.Contains(m.PersistentId));
                if (g != null) {
                    vm.IsGroup = true;
                    vm.GroupName = g.Name;
                    vm.StatusText = "组 \"" + g.Name + "\"";
                    vm.StatusIcon = "";
                } else {
                    vm.IsGroup = false;
                    vm.StatusText = cfg.Kind == WallpaperKind.Video ? "独立 (视频)" : "独立 (图片)";
                    vm.StatusIcon = "";
                }
            } else {
                vm.IsGroup = false;
                vm.StatusText = "未设置";
                vm.StatusIcon = "";
            }
            return vm;
        }).ToList();
        MonitorList.ItemsSource = vms;
        var idx = vms.FindIndex(v => v.PersistentId == _selectedMonitor);
        MonitorList.SelectedIndex = idx >= 0 ? idx : (vms.Count > 0 ? 0 : -1);
        _suppressEvents = false;
    }

    private void MonitorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var vm = MonitorList.SelectedItem as MonitorVm;
        _selectedMonitor = vm?.PersistentId;
        UpdateClearGroupBtn();
    }

    /// <summary>「移除组壁纸」按钮：选中屏被有壁纸的组覆盖时才显示。</summary>
    private void UpdateClearGroupBtn()
    {
        if (ClearGroupWallpaperBtn is null) return;
        ClearGroupWallpaperBtn.Visibility = _selectedMonitor is not null && _host.Groups.Any(g =>
            !string.IsNullOrWhiteSpace(g.WallpaperPath) && g.MonitorIds.Contains(_selectedMonitor))
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RemoveCoveringGroupWallpaper_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMonitor is null) return;
        var covering = _host.Groups.FirstOrDefault(g =>
            !string.IsNullOrWhiteSpace(g.WallpaperPath) && g.MonitorIds.Contains(_selectedMonitor));
        if (covering is null) return;
        var r = MessageBox.Show(this,
            $"清空显示组「{covering.Name}」的组壁纸？\n组内所有屏幕将回退到各自的独立壁纸。",
            "移除组壁纸", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
        var updated = _host.Groups.Select(g => g.Id == covering.Id ? g with { WallpaperPath = "" } : g).ToList();
        _host.SetDisplayGroups(updated);
        _groups = updated.ToList();
        RefreshGroupsUI();
        RefreshMonitorList();
    }

    private void SetMonitorWallpaper_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMonitor is null) { MessageBox.Show(this, "先选择一个屏幕", "设置壁纸"); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择本屏壁纸（图片/视频/GIF）",
            Filter = "壁纸|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.mp4;*.wmv;*.avi;*.m4v|所有文件|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        // 组壁纸优先级高于独立壁纸（M5 语义）：该屏在有壁纸的组里时独立壁纸会被覆盖（真机踩坑），
        // 必须让用户显式选择「移出组」或「改设组壁纸」。
        var covering = _host.Groups.FirstOrDefault(g =>
            !string.IsNullOrWhiteSpace(g.WallpaperPath) && g.MonitorIds.Contains(_selectedMonitor));
        if (covering is not null)
        {
            var r = MessageBox.Show(this,
                $"该屏属于显示组「{covering.Name}」，当前生效的是组壁纸：\n{covering.WallpaperPath}\n\n" +
                "组壁纸会覆盖每屏独立壁纸。要把该屏从组中移除并应用独立壁纸吗？\n（选“否”将取消本次设置）",
                "该屏被显示组覆盖", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            var updated = _host.Groups.Select(g => g.MonitorIds.Contains(_selectedMonitor)
                ? g with { MonitorIds = g.MonitorIds.Where(id => id != _selectedMonitor).ToList() }
                : g).ToList();
            _host.SetDisplayGroups(updated);
            _groups = updated.ToList();
            RefreshGroupsUI();
        }

        _host.SetWallpaper(_selectedMonitor, dlg.FileName);
        RefreshMonitorList();
        UpdateClearGroupBtn();
    }

    private void RemoveMonitorWallpaper_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMonitor is null) return;
        _host.RemoveWallpaper(_selectedMonitor);
        RefreshMonitorList();
    }

    // ---------- 显示组管理 ----------

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
        RefreshMonitorList(); // 组壁纸变化影响每屏状态显示
    }
}
