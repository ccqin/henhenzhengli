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
        // DetectState：区分首次启动 vs 崩溃恢复（HideIcons 残留）。M1 占位日志，M2 加 UI 提示（I-2）。
        var state = _recoveryGuard.DetectState();
        if (state == RecoveryState.PreviouslyTakenOver)
        {
            System.Diagnostics.Debug.WriteLine("上次异常退出，接管状态恢复中");
        }
        _recoveryGuard.TakeOver();

        // 2-4. 接线（图标层 / 同步 / explorer 重启 watcher）。任一抛异常必须回滚 HideIcons，
        // 否则 explorer 原生图标隐藏但 app 没起来 → 桌面空（风险登记册 #1，I-1）。
        try
        {
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
            // BeginInvoke 异步投递：防 UI 线程阻塞时与 OnExit 的 sync.Dispose() 理论死锁（I-5）。
            _sync.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(() => _iconLayer.SetIcons(_sync.Current)));

            // 4. explorer 重启：TaskbarCreated → 重新接管（Attach 到 iconLayer hwnd）
            _shellWatcher = new ShellRestartWatcher();
            _shellWatcher.ExplorerRestarted += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                // TakeOver 抛异常会 marshal 回 UI 线程到 DispatcherUnhandledException → crash → 反触发桌面空（I-6）。
                try { _recoveryGuard.TakeOver(); }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ExplorerRestarted TakeOver 失败：{ex}");
                    try { _recoveryGuard.RestoreExplorer(); } catch { /* RestoreExplorer 也失败则不再升级 */ }
                }
            }));
            _shellWatcher.Attach(iconHwnd);
        }
        catch
        {
            // 回滚 HideIcons → 让 explorer 原生图标回来；rethrow 走 WPF 默认未处理流程。
            try { _recoveryGuard?.RestoreExplorer(); } catch { /* 已尽力 */ }
            throw;
        }
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
