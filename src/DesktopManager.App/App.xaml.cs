using System.Windows;
using H.NotifyIcon;

namespace DesktopManager.App;

public partial class App : Application
{
    private TaskbarIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _tray = (TaskbarIcon)FindResource("TrayIcon");
        _tray.ForceCreate();
    }

    private void OnExit_Clicked(object sender, RoutedEventArgs e)
    {
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
