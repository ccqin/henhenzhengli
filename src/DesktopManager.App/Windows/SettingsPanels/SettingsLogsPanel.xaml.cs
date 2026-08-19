using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using DesktopManager.App.Services;

namespace DesktopManager.App.Windows.SettingsPanels;

/// <summary>设置页签：日志与操作（M6 重构③ 拆分自 SettingsWindow）。</summary>
public partial class SettingsLogsPanel : UserControl
{
    public SettingsLogsPanel() => InitializeComponent();

    public void RefreshLogs()
    {
        int days = LogDaysFilter?.SelectedIndex switch { 0 => 1, 2 => 7, 3 => 30, _ => 3 };
        string minLevel = LogLevelFilter?.SelectedIndex switch
        {
            1 => "OPS",   // 只看操作
            2 => "ERR",   // 只看错误
            3 => "WRN",   // 警告+错误
            _ => "DBG",   // 全部
        };
        var rows = LogDb.Query(days, minLevel);
        LogGrid.ItemsSource = rows;
        LogCount.Text = $"{rows.Count} 条（近 {days} 天）";
    }

    private void LogFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsVisible) RefreshLogs();
    }

    private void LogRefresh_Click(object sender, RoutedEventArgs e) => RefreshLogs();

    private void LogExport_Click(object sender, RoutedEventArgs e)
    {
        int days = LogDaysFilter?.SelectedIndex switch { 0 => 1, 2 => 7, 3 => 30, _ => 3 };
        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "文本文件|*.txt",
            FileName = $"DesktopManager-日志-{DateTime.Now:yyyyMMdd-HHmm}.txt",
        };
        if (sfd.ShowDialog(Window.GetWindow(this)) == true)
        {
            try
            {
                System.IO.File.WriteAllLines(sfd.FileName, LogDb.Export(days));
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(sfd.FileName) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"导出失败：{ex.Message}", "日志", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void LogClear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this), "确定清空全部日志与操作记录？", "清空确认",
            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        LogDb.Clear();
        RefreshLogs();
    }
}
