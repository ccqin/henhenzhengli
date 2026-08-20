using System.Windows;
using System.Windows.Controls;
using DesktopManager.Core.Models;

namespace DesktopManager.App.Windows.SettingsPanels;

/// <summary>设置页签：外观（M6 重构③ 拆分自 SettingsWindow）。需要 Host（主进程 MultiMonitorHost）。</summary>
public partial class SettingsAppearancePanel : UserControl
{
    public SettingsAppearancePanel() => InitializeComponent();

    /// <summary>宿主引用（SettingsWindow 构造后注入）。</summary>
    public MultiMonitorHost? Host { get; set; }

    private bool _suppress;

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (Host is null || _suppress) return;
        Host.SetAutoStart(AutoStartBox.IsChecked == true);
    }

    public void LoadAppearanceUI()
    {
        if (Host is null || _suppress) return;
        _suppress = true;
        AutoStartBox.IsChecked = Host.AutoStartEnabled;
        var a = Host.Appearance;
        foreach (var rb in new[] { IconSizeS, IconSizeM, IconSizeL })
            rb.IsChecked = rb.Tag.ToString() == a.IconSize.ToString();
        LabelShadow.IsChecked = a.LabelStyle == "shadow";
        LabelPill.IsChecked = a.LabelStyle == "pill";
        PreviewIconSize.Text = a.IconSize.ToString();
        UpdatePreviewLabel();
        _suppress = false;
    }

    private void IconSize_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppress || Host is null) return;
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var size))
        {
            Host.SetAppearance(size, LabelShadow.IsChecked == true ? "shadow" : "pill");
            PreviewIconSize.Text = size.ToString();
        }
    }

    private void LabelStyle_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppress || Host is null) return;
        Host.SetAppearance(int.TryParse(PreviewIconSize.Text, out var sz) ? sz : 48,
            LabelShadow.IsChecked == true ? "shadow" : "pill");
        UpdatePreviewLabel();
    }

    /// <summary>预览标签：shadow=透明底+文字阴影；pill=胶囊底。</summary>
    private void UpdatePreviewLabel()
    {
        bool shadow = LabelShadow.IsChecked == true;
        PreviewLabel.Background = shadow
            ? System.Windows.Media.Brushes.Transparent
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 0, 0)) { Opacity = 0.4 };
    }
}
