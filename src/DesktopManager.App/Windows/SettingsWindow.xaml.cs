using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DesktopManager.Core.Models;
using DesktopManager.Native;

namespace DesktopManager.App.Windows;

/// <summary>M5-T2：设置窗口（单实例，托盘入口）。左：屏幕排列预览（等比矩形；T5 加拖拽+应用拓扑）；
/// 右：显示组管理（建组/删组/勾选成员/设组壁纸）。所有变更即时 commit 到 host（重渲染+防抖落盘）。</summary>
public partial class SettingsWindow : Window
{
    private readonly MultiMonitorHost _host;
    private List<DisplayGroup> _groups;
    private List<MonitorInfo> _monitors = new();
    private int _selectedGroup = -1;
    private bool _suppressEvents;

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

    // ---------- 排列预览 ----------

    private void RefreshMonitors()
    {
        _monitors = MonitorEnumerator.Enumerate().ToList();
        PreviewCanvas.Children.Clear();
        if (_monitors.Count == 0) return;

        double virtL = _monitors.Min(m => m.X);
        double virtT = _monitors.Min(m => m.Y);
        double virtW = _monitors.Max(m => m.X + m.Width) - virtL;
        double virtH = _monitors.Max(m => m.Y + m.Height) - virtT;
        double scale = Math.Min((PreviewW - 16) / virtW, (PreviewH - 16) / virtH);
        double offX = (PreviewW - virtW * scale) / 2;
        double offY = (PreviewH - virtH * scale) / 2;

        foreach (var m in _monitors)
        {
            var rect = new Rectangle
            {
                Width = m.Width * scale,
                Height = m.Height * scale,
                Fill = new SolidColorBrush(Color.FromRgb(0x2E, 0x3A, 0x5E)),
                Stroke = new SolidColorBrush(m.IsPrimary ? Colors.Gold : Colors.SteelBlue),
                StrokeThickness = 2,
                Tag = m.PersistentId
            };
            Canvas.SetLeft(rect, (m.X - virtL) * scale + offX);
            Canvas.SetTop(rect, (m.Y - virtT) * scale + offY);
            PreviewCanvas.Children.Add(rect);

            var label = new TextBlock
            {
                Text = $"{ShortName(m.PersistentId)}{(m.IsPrimary ? " ★" : "")}\n{m.Width}x{m.Height}",
                Foreground = new SolidColorBrush(Colors.WhiteSmoke),
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, (m.X - virtL) * scale + offX + 6);
            Canvas.SetTop(label, (m.Y - virtT) * scale + offY + 6);
            PreviewCanvas.Children.Add(label);
        }
    }

    private static string ShortName(string persistentId)
    {
        var i = persistentId.LastIndexOf('#');
        return i >= 0 ? persistentId[(i + 1)..] : persistentId;
    }

    private void RefreshMonitors_Click(object sender, RoutedEventArgs e) => RefreshMonitors();
    private void ResetPreview_Click(object sender, RoutedEventArgs e) => RefreshMonitors();

    private void ApplyArrangement_Click(object sender, RoutedEventArgs e)
    {
        // M5-T5 实现（拖拽排列应用到 Windows 拓扑）。
        MessageBox.Show(this, "M5-T5 实现中", "应用排列");
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
            MonitorIds = _monitors.Select(m => m.PersistentId).ToList() // 默认全选在线屏，用户再勾掉
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

    /// <summary>commit 到 host：即时重渲染壁纸窗 + 防抖落盘；刷新本窗 UI。</summary>
    private void Commit()
    {
        _host.SetDisplayGroups(_groups);
        RefreshGroupsUI();
    }
}
