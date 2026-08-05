using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DesktopManager.Core.Models;
using DesktopManager.Native;

namespace DesktopManager.App.Windows;

/// <summary>M5-T2（M5-T5 修订）：设置窗口（单实例，托盘入口）。
/// 左：屏幕排列只读预览（等比矩形；拖拽改拓扑功能因本机显示栈拒绝第三方变更而移除）。
/// 右：显示组管理（组内屏共享壁纸）+ 每屏独立壁纸设置（原桌面右键入口收归此处）。</summary>
public partial class SettingsWindow : Window
{
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
        RefreshMonitors();
        RefreshGroupsUI();
        RefreshMonitorList();
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
            els.Label.Text = $"{ShortName(m.PersistentId)}{(m.IsPrimary ? " ★" : "")}\n{r.Width}x{r.Height}";
            Canvas.SetLeft(els.Label, (r.Left - _virtL) * _scale + _offX + 6);
            Canvas.SetTop(els.Label, (r.Top - _virtT) * _scale + _offY + 6);
        }
    }

    private void RefreshMonitors_Click(object sender, RoutedEventArgs e)
    {
        RefreshMonitors();
        RefreshMonitorList();
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
        MonitorList.ItemsSource = _monitors
            .Select(m => $"{ShortName(m.PersistentId)}（{m.Width}x{m.Height}）— {MonitorWallpaperText(m.PersistentId)}")
            .ToList();
        var idx = _monitors.FindIndex(m => m.PersistentId == _selectedMonitor);
        MonitorList.SelectedIndex = idx >= 0 ? idx : (_monitors.Count > 0 ? 0 : -1);
        _suppressEvents = false;
    }

    private string MonitorWallpaperText(string monId)
    {
        var g = _host.Groups.FirstOrDefault(g =>
            !string.IsNullOrWhiteSpace(g.WallpaperPath) && g.MonitorIds.Contains(monId));
        if (g is not null) return $"组「{g.Name}」壁纸";
        return "(独立，未设置)";
    }

    private void MonitorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _selectedMonitor = MonitorList.SelectedIndex >= 0 && MonitorList.SelectedIndex < _monitors.Count
            ? _monitors[MonitorList.SelectedIndex].PersistentId
            : null;
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
        _host.SetWallpaper(_selectedMonitor, dlg.FileName);
        RefreshMonitorList();
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
