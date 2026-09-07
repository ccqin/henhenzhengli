using DesktopManager.App;

using System.Windows.Controls;
using DesktopManager.App.Windows.SettingsPanels;
namespace DesktopManager.App.Windows.SettingsPanels;

/// <summary>插件列表行 VM（UI 用）。</summary>
public sealed class PluginRow : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Source { get; init; } = "";
    public string Status { get; init; } = "";
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; PropertyChanged?.Invoke(this, new(nameof(Enabled))); }
    }
    private bool _enabled;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>插件管理页：发现列表 + 启停（T1：仅开关；插件自定义配置 UI 为 T3）。</summary>
public partial class SettingsPluginsPanel : UserControl
{
    public MultiMonitorHost? Host { get; set; }
    private bool _suppress;

    public SettingsPluginsPanel()
    {
        InitializeComponent();
    }

    public void LoadPluginsUI()
    {
        if (Host is null || _suppress) return;
        _suppress = true;
        var list = Host.GetPluginsView();
        PluginList.ItemsSource = list;
        foreach (var row in list)
        {
            row.PropertyChanged += (_, _) =>
            {
                if (_suppress) return;
                Host.SetPluginEnabled(row.Id, row.Enabled);  // 开关即启停 + 持久化
                LoadPluginsUI(); // 刷新状态行
            };
        }
        _suppress = false;
    }
}
