using System.Windows;
using DesktopManager.Core.Services;
using DesktopManager.App.Windows;
using H.NotifyIcon;

namespace DesktopManager.App;

public partial class App : Application
{
    private TaskbarIcon? _tray;
    private RecoveryGuard? _recoveryGuard;
    private IconLayerWindow? _iconLayer;
    private DesktopSync? _sync;
    private ShellRestartWatcher? _shellWatcher;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _tray = (TaskbarIcon)FindResource("TrayIcon");
        _tray.ForceCreate();

        // 1. 恢复 + 接管：隐藏 explorer 原生桌面图标
        _recoveryGuard = new RecoveryGuard();
        _recoveryGuard.TakeOver();

        // 2. 图标层窗口（不点击穿透，可点图标）
        _iconLayer = new IconLayerWindow();
        _iconLayer.Show();
        var iconHwnd = new System.Windows.Interop.WindowInteropHelper(_iconLayer).Handle;

        // 3. 桌面同步：初始 SetIcons + Changed 事件 Dispatcher 回 SetIcons
        var snapshot = DesktopSnapshot.ForDefaultDesktops();
        _iconLayer.SetIcons(snapshot.Capture());
        _sync = new DesktopSync(
            snapshot,
            new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            },
            TimeSpan.FromSeconds(3));
        _sync.Changed += (_, _) => Dispatcher.Invoke(() => _iconLayer.SetIcons(_sync.Current));

        // 4. explorer 重启：TaskbarCreated → 重新接管（Attach 到 iconLayer hwnd）
        _shellWatcher = new ShellRestartWatcher();
        _shellWatcher.ExplorerRestarted += () => Dispatcher.Invoke(() => _recoveryGuard.TakeOver());
        _shellWatcher.Attach(iconHwnd);
    }

    private void OnExit_Clicked(object sender, RoutedEventArgs e)
    {
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _sync?.Dispose();
        _recoveryGuard?.RestoreExplorer(); // 正常退出恢复 explorer 原生桌面图标
        _tray?.Dispose();
        base.OnExit(e);
    }
}
