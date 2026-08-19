using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopManager.Core.Models;

namespace DesktopManager.App.Windows.SettingsPanels;

/// <summary>设置页签：右键菜单（M6 重构③ 拆分自 SettingsWindow）。需要 Host。</summary>
public partial class SettingsMenuPanel : UserControl
{
    public SettingsMenuPanel() => InitializeComponent();

    public MultiMonitorHost? Host { get; set; }

    private bool _suppressMenu;
    private List<CustomMenuItem> _menuItems = new();
    private List<string> _sysHidden = new();

    public void LoadMenuUI()
    {
        if (Host is null || _suppressMenu) return;
        _suppressMenu = true;
        var m = Host.Menu;
        MenuOpen.IsChecked = m.ShowOpen;
        MenuRename.IsChecked = m.ShowRename;
        MenuDelete.IsChecked = m.ShowDelete;
        MenuLocate.IsChecked = m.ShowLocate;
        MenuSystem.IsChecked = m.ShowSystemMenu;
        _menuItems = m.CustomItems.ToList();
        CustomMenuList.ItemsSource = _menuItems;
        _sysHidden = m.SystemMenuHidden.ToList();
        SysHiddenList.ItemsSource = _sysHidden;
        _suppressMenu = false;
    }

    private void CommitMenu()
    {
        if (Host is null || _suppressMenu) return;
        Host.SetMenuConfig(new MenuConfig
        {
            ShowOpen = MenuOpen.IsChecked == true,
            ShowRename = MenuRename.IsChecked == true,
            ShowDelete = MenuDelete.IsChecked == true,
            ShowLocate = MenuLocate.IsChecked == true,
            ShowSystemMenu = MenuSystem.IsChecked == true,
            CustomItems = _menuItems.ToList(),
            SystemMenuHidden = _sysHidden.ToList(),
        });
    }

    private void MenuFlag_Changed(object sender, RoutedEventArgs e) => CommitMenu();

    private void CustomMenuList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void CustomMenuAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CustomMenuItemDialog(Window.GetWindow(this)) { Title = "添加自定义菜单项" };
        if (dlg.ShowDialog() == true)
        {
            _menuItems.Add(dlg.Result);
            CustomMenuList.Items.Refresh();
            CommitMenu();
        }
    }

    private void CustomMenuEdit_Click(object sender, RoutedEventArgs e)
    {
        if (CustomMenuList.SelectedItem is not CustomMenuItem item) return;
        var dlg = new CustomMenuItemDialog(Window.GetWindow(this)) { Title = "编辑自定义菜单项", Result = item };
        if (dlg.ShowDialog() == true)
        {
            var idx = _menuItems.IndexOf(item);
            if (idx >= 0) _menuItems[idx] = dlg.Result;
            CustomMenuList.Items.Refresh();
            CommitMenu();
        }
    }

    private void CustomMenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (CustomMenuList.SelectedItem is not CustomMenuItem item) return;
        _menuItems.Remove(item);
        CustomMenuList.Items.Refresh();
        CommitMenu();
    }

    // 系统菜单隐藏项
    private void SysHiddenAdd_Click(object sender, RoutedEventArgs e) => AddSysHidden();
    private void SysHiddenBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) AddSysHidden();
    }
    private void SysHiddenList_DoubleClick(object sender, MouseButtonEventArgs e) { }

    private void AddSysHidden()
    {
        var t = SysHiddenBox.Text.Trim();
        if (t.Length == 0 || _sysHidden.Contains(t)) return;
        _sysHidden.Add(t);
        SysHiddenBox.Clear();
        SysHiddenList.Items.Refresh();
        CommitMenu();
    }

    private void SysHiddenRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string t)
        {
            _sysHidden.Remove(t);
            SysHiddenList.Items.Refresh();
            CommitMenu();
        }
    }

    /// <summary>枚举系统菜单项（以桌面第一个文件为例），弹列表供点选加入隐藏。</summary>
    private void SysMenuEnumerate_Click(object sender, RoutedEventArgs e)
    {
        string sample = System.IO.Directory.EnumerateFiles(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop)).FirstOrDefault()
            ?? "C:\\Windows\\notepad.exe";
        List<string> items;
        try { items = DesktopManager.Native.SystemContextMenu.EnumerateTopLevel(sample); }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), $"枚举失败：{ex.Message}", "系统菜单", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (items.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "未枚举到菜单项", "系统菜单", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var hint = new TextBlock
        {
            Text = $"「{System.IO.Path.GetFileName(sample)}」的系统菜单项（点击加入隐藏）：",
            Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(12),
        };
        var list = new ListBox { Margin = new Thickness(12, 0, 12, 12), MaxHeight = 380 };
        foreach (var it in items)
        {
            var row = new ListBoxItem { Content = it, Foreground = System.Windows.Media.Brushes.White };
            row.MouseLeftButtonUp += (_, _) =>
            {
                SysHiddenBox.Text = it;
                AddSysHidden();
            };
            list.Items.Add(row);
        }
        var dlg = new Window
        {
            Title = "系统菜单项", Owner = Window.GetWindow(this), Width = 420, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1B, 0x1B, 0x26)),
            Content = new StackPanel { Children = { hint, list } },
        };
        dlg.ShowDialog();
    }
}
