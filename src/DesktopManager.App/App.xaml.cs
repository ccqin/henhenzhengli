using System.IO;
using System.Windows;
using DesktopManager.App.Logging;
using DesktopManager.App.Windows;
using DesktopManager.Core.Services;
using H.NotifyIcon;
using Serilog;

namespace DesktopManager.App;

public partial class App : Application
{
    private TaskbarIcon? _tray;
    private RecoveryGuard? _recoveryGuard;
    private IconLayerWindow? _iconLayer;
    private DesktopSync? _sync;
    private ShellRestartWatcher? _shellWatcher;

    /// <summary>T7 配置文件路径：%AppData%\DesktopManager\config.json（与 I-3 RunOnce 同属用户级状态目录）。</summary>
    private static string GetConfigPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "DesktopManager", "config.json");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // P1：日志必须最先初始化——后续所有诊断（含 --restore-icons 分支）都依赖 Log.Logger。
        LogConfig.Init();

        // C1（I-3 致命项）：--restore-icons 模式。RunOnce 触发的崩溃恢复路径。
        // 最早期检测（在 tray/window 创建之前）：调 RestoreExplorer（= ShowDesktopIcons，含 WM_SETTINGCHANGE 广播）
        // → HideIcons=0 且广播刷新 explorer → 桌面图标恢复 → Shutdown 退出。
        // 广播消除 RunOnce/explorer 时序依赖（无论 explorer 是否已读 HideIcons=1 都会刷新）。
        // **此模式绝不接管、不建窗口、不跑 sync**——只恢复后退出。
        bool isRestoreMode = e.Args.Length > 0 && Array.IndexOf(e.Args, "--restore-icons") >= 0;
        Log.Information("DesktopManager 启动，模式={Mode}", isRestoreMode ? "restore-icons" : "normal");
        if (isRestoreMode)
        {
            try
            {
                Log.Information("--restore-icons 模式：恢复桌面图标（含 WM_SETTINGCHANGE 广播）后退出。");
                var restoreGuard = new RecoveryGuard();
                restoreGuard.RestoreExplorer();
            }
            catch (System.Exception ex)
            {
                // 恢复失败也退出（不接管）；最坏情况下次正常启动会再 DetectState/Restore。
                Log.Error(ex, "--restore-icons 恢复失败");
            }
            // ShutdownMode=OnExplicitShutdown（App.xaml）：显式 Shutdown 才退出。
            // 此分支未创建任何 window/tray，OnExit 里 _iconLayer/_sync/_tray/_recoveryGuard 字段全 null，全 ?. no-op，安全。
            Shutdown();
            return;
        }

        _tray = (TaskbarIcon)FindResource("TrayIcon");
        _tray.ForceCreate();

        // 1. 恢复 + 接管：隐藏 explorer 原生桌面图标
        _recoveryGuard = new RecoveryGuard();
        // DetectState：区分首次启动 vs 崩溃恢复（HideIcons 残留）。M1 占位日志，M2 加 UI 提示（I-2）。
        var state = _recoveryGuard.DetectState();
        if (state == RecoveryState.PreviouslyTakenOver)
        {
            Log.Information("上次异常退出，接管状态恢复中");
        }
        // I1：TakeOver + SetSelfCleanupOnExit 包 try/catch。
        // - TakeOver 部分成功（HideIcons 已设 1）后抛 → catch 回滚 RestoreExplorer（HideIcons 恢复 0，含广播）→ rethrow 走 WPF 未处理流程，
        //   避免桌面空（reviewer 关切：接管半成功后抛未回滚）。
        // - SetSelfCleanupOnExit 内部已 try/catch 吞异常（不阻断启动），外层兜其包装/未知失败。
        try
        {
            _recoveryGuard.TakeOver();
            // I-3 自清理：接管成功（HideIcons 已设 1）后立即写 RunOnce 兜底（= 启动 app --restore-icons 命令）。
            // 后续接线任何失败/崩溃都保留 RunOnce，下次登录 app 广播恢复 HideIcons=0（幂等无害）。
            _recoveryGuard.SetSelfCleanupOnExit();
        }
        catch (System.Exception)
        {
            try { _recoveryGuard.RestoreExplorer(); } catch { /* 回滚失败则尽力，不再升级 */ }
            throw;
        }

        // 2-4. 接线（图标层 / 同步 / explorer 重启 watcher）。任一抛异常必须回滚 HideIcons，
        // 否则 explorer 原生图标隐藏但 app 没起来 → 桌面空（风险登记册 #1，I-1）。
        try
        {
            // T7：创建 ConfigStore（原子写 + 异常兜底），注入 IconLayerWindow（加载 Fences + 防抖 Save）。
            var configStore = new ConfigStore(GetConfigPath());
            // 2. 图标层窗口（不点击穿透，可点图标）
            _iconLayer = new IconLayerWindow(configStore);
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
                try { _recoveryGuard.TakeOver(); _recoveryGuard.SetSelfCleanupOnExit(); /* 重新接管后重写 RunOnce（场景4） */ }
                catch (System.Exception ex)
                {
                    Log.Error(ex, "ExplorerRestarted TakeOver 失败");
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
        // T7：立即保存布局（不等防抖），确保退出时 Fences 布局/归属/折叠态落盘。
        // 放 _sync.Dispose / RestoreExplorer 之前：保存是 app 职责的核心数据，优先级最高。
        // 保存失败不阻塞后续恢复流程（SaveFencesNow 内部 try/catch）。
        try { _iconLayer?.SaveFencesNow(); }
        catch (System.Exception ex) { Log.Error(ex, "OnExit SaveFencesNow 失败"); }

        _sync?.Dispose();
        // I-3 时序：RestoreExplorer 成功（HideIcons 已 0）后才 ClearSelfCleanup。
        // 若 RestoreExplorer 失败（HideIcons 可能仍 1）→ 保留 RunOnce 兜底，让下次登录 app --restore-icons 模式广播恢复，避免桌面永久空。
        try
        {
            _recoveryGuard?.RestoreExplorer(); // 正常退出恢复 explorer 原生桌面图标
            _recoveryGuard?.ClearSelfCleanup(); // 清 RunOnce 钩子（正常退出无需下次登录兜底）
        }
        catch (System.Exception ex)
        {
            // 可恢复的降级路径：桌面图标恢复失败时保留 RunOnce 兜底（下次登录自动修复）→ Warning。
            Log.Warning(ex, "OnExit RestoreExplorer/ClearSelfCleanup 失败，保留 RunOnce 兜底");
        }
        _tray?.Dispose();
        LogConfig.Shutdown(); // P1：base.OnExit 之前 flush 日志
        base.OnExit(e);
    }
}
