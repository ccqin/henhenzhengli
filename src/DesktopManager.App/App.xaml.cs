using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using DesktopManager.App.Logging;
using DesktopManager.App.Windows;
using DesktopManager.Core.Services;
using DesktopManager.Native;
using H.NotifyIcon;
using Serilog;

namespace DesktopManager.App;

public partial class App : Application
{
    // 单实例 Mutex：主实例启动时持有。
    // --restore-icons 模式启动时先探测：主实例在跑则直接退出（不恢复不重启 explorer）。
    // 真机根因（explorer 无限重启循环）：RunOnce 由 explorer.exe **每次作为 shell 启动时**执行（不只是登录）。
    // 手动重启 explorer → 新 explorer 跑 RunOnce(--restore-icons) → 同时主实例的 ShellRestartWatcher
    // 收到 TaskbarCreated 重写 RunOnce → --restore-icons 杀 explorer → 新 explorer 又跑 RunOnce → 无限循环。
    // Mutex 守卫断环：主实例在跑时 --restore-icons no-op（主实例退出时会自己 RestoreExplorer）。
    private const string SingleInstanceMutexName = @"Local\DesktopManager.SingleInstance";
    private Mutex? _singleInstanceMutex;

    private TaskbarIcon? _tray;
    private RecoveryGuard? _recoveryGuard;
    private MultiMonitorHost? _host;
    private DesktopSync? _sync;
    private ShellRestartWatcher? _shellWatcher;
    private DisplayChangeWatcher? _displayWatcher;
    private PlaybackGovernor? _governor;
    private SettingsWindow? _settingsWindow;
    /// <summary>T7 配置文件路径：%AppData%\DesktopManager\config.json（与 I-3 RunOnce 同属用户级状态目录）。</summary>
    private static string GetConfigPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "DesktopManager", "config.json");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // P1：日志必须最先初始化——后续所有诊断（含 --restore-icons 分支）都依赖 Log.Logger。
        LogConfig.Init();

        // M6.3 前置（真机需要）：全局异常落日志。此前 explorer 重启时 app 静默崩溃无任何痕迹，
        // 无兜底无法定位。三入口全接：UI 线程 / 非 UI 线程 / Task。
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "UI 线程未处理异常（app 将退出）");
            LogConfig.Shutdown();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as System.Exception, "非 UI 线程未处理异常（app 将退出，isTerminating={T}）", args.IsTerminating);
            LogConfig.Shutdown();
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Task 未观察异常（不退出）");
            args.SetObserved();
        };

        // C1（I-3 致命项）：--restore-icons 模式。RunOnce 触发的崩溃恢复路径。
        // 最早期检测（在 tray/window 创建之前）：调 RestoreExplorer（= ShowDesktopIcons，含 WM_SETTINGCHANGE 广播）
        // → HideIcons=0 且广播刷新 explorer → 桌面图标恢复 → Shutdown 退出。
        // 广播消除 RunOnce/explorer 时序依赖（无论 explorer 是否已读 HideIcons=1 都会刷新）。
        // **此模式绝不接管、不建窗口、不跑 sync**——只恢复后退出。
        bool isRestoreMode = e.Args.Length > 0 && Array.IndexOf(e.Args, "--restore-icons") >= 0;
        Log.Information("DesktopManager 启动，模式={Mode}", isRestoreMode ? "restore-icons" : "normal");

        // M3-T1 诊断：--debug-monitors 模式。枚举显示器（含持久 ID）打印后退出，不接管不建窗。
        // 验收用途：交换显示器排列顺序/插拔前后各跑一次，对比 PersistentId 是否稳定。
        if (e.Args.Length > 0 && Array.IndexOf(e.Args, "--debug-monitors") >= 0)
        {
            foreach (var m in MonitorEnumerator.Enumerate())
            {
                Console.WriteLine(
                    $"{(m.IsPrimary ? "[主屏] " : "")}{m.DeviceName} | PersistentId={m.PersistentId} | " +
                    $"全屏=({m.X},{m.Y}) {m.Width}x{m.Height} | 工作区=({m.WorkX},{m.WorkY}) {m.WorkWidth}x{m.WorkHeight}");
                Log.Information("--debug-monitors: {Device} {PersistentId} ({X},{Y} {W}x{H}) primary={P}",
                    m.DeviceName, m.PersistentId, m.X, m.Y, m.Width, m.Height, m.IsPrimary);
            }
            Log.Information("--debug-monitors 拓扑诊断: {Diag}", DesktopManager.Native.DisplayTopologyApplier.Diagnose());
            Shutdown();
            return;
        }

        if (isRestoreMode)
        {
            // 断环守卫：主实例在跑 → 接管/恢复由主实例自己负责（退出时 RestoreExplorer），
            // 这里若继续恢复+杀 explorer 会与主实例互踩成无限循环（见 SingleInstanceMutexName 注释）。
            try
            {
                using var probe = Mutex.OpenExisting(SingleInstanceMutexName);
                Log.Information("--restore-icons：主实例在运行，跳过恢复直接退出（防循环）");
                Shutdown();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 主实例不在跑（崩溃/被杀后的真恢复场景）：继续下面的恢复流程。
            }

            try
            {
                Log.Information("--restore-icons 模式：恢复桌面图标（含 WM_SETTINGCHANGE 广播）后退出。");
                var restoreGuard = new RecoveryGuard();
                restoreGuard.RestoreExplorer();
                // I-3 真机根因：登录时 explorer 刚启动未就绪 → RestoreExplorer 的 WM_SETTINGCHANGE 广播被时序吞
                // （真机证据：日志确认 --restore-icons 跑了、HideIcons 已写 0，但桌面图标一直空）。
                // 修法：强制重启 explorer 进程 → 新 explorer 启动时重新读 HideIcons=0 → 桌面图标显示。
                // 不再靠广播（explorer 未就绪时不可靠），改让 explorer 重读注册表，消除时序赌博。
                // AutoRestartShell 自动拉起 explorer；若被禁则 ExplorerRestarter 内部兜底主动 Start。仅此崩溃恢复路径调用（disruptive：任务栏闪一下）。
                ExplorerRestarter.Restart();
                // 真机实验新发现（高频采样）：垂死 explorer 退出时会把内存「隐藏」状态冲刷回注册表
                // （kill 后 ~260ms 把我们的 0 改回 1）→ 新 explorer 读到 1 仍隐藏图标。
                // 对策：新 explorer 起来后再刷一次（注册表 0 + SW_SHOW 新 ListView）。
                // 运行中 explorer 不会改写值（实验证实），第二次写入稳定。
                restoreGuard.RestoreExplorerAfterShellRestart();
            }
            catch (System.Exception ex)
            {
                // 恢复失败也退出（不接管）；最坏情况下次正常启动会再 DetectState/Restore。
                Log.Error(ex, "--restore-icons 恢复失败");
            }
            // ShutdownMode=OnExplicitShutdown（App.xaml）：显式 Shutdown 才退出。
            // 此分支未创建任何 window/tray，OnExit 里 _host/_sync/_tray/_recoveryGuard 字段全 null，全 ?. no-op，安全。
            Shutdown();
            return;
        }

        _tray = (TaskbarIcon)FindResource("TrayIcon");
        _tray.ForceCreate();

        // 单实例守卫：防双开导致双重接管（HideIcons/ListView 状态互踩）。
        // 已有主实例则直接退出（不接管）。Mutex 由字段持有至进程退出；OnExit 里 Dispose。
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            Log.Warning("已有主实例在运行，本次启动退出（防双重接管）");
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

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
            // M3-T3/T4：ConfigStore 收归 MultiMonitorHost（聚合所有窗口 + 孤儿配置防抖落盘）。
            var configStore = new ConfigStore(GetConfigPath());
            // 2. 每屏一个图标层窗口（按 MonitorAssignment 切分 Fence/位置，定位各自工作区）
            _host = new MultiMonitorHost(configStore);
            _host.Attach();

            // 3. 桌面同步：仍是单一全局 watcher（桌面是单一逻辑空间，不按屏拆）；
            //    初始全量按归属分发，Changed 增量经 host.Dispatch 路由到各窗口。
            var snapshot = DesktopSnapshot.ForDefaultDesktops();
            // 桌面系统图标（shell 虚拟对象，文件系统枚举拿不到；CLSID 路径走 SHGetFileInfo 取图标、
            // Process.Start 打开）。位置持久化天然工作（IconPosition 以 CLSID 路径存取）。
            var shellIcons = new[]
            {
                new Core.Models.IconItem("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "此电脑"),
                new Core.Models.IconItem("::{645FF040-5081-101B-9F08-00AA002F954E}", "回收站"),
            };
            _host.ApplyInitialSnapshot(snapshot.Capture().Concat(shellIcons).ToList());
            _sync = new DesktopSync(
                snapshot,
                new[] {
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                },
                TimeSpan.FromSeconds(3));
            // BeginInvoke 异步投递：防 UI 线程阻塞时与 OnExit 的 sync.Dispose() 理论死锁（I-5）。
            _sync.Changed += (_, diff) => Dispatcher.BeginInvoke(new Action(() => _host.Dispatch(diff)));

            // 4. explorer 重启：TaskbarCreated → 重新接管（Attach 到主屏窗口 hwnd）
            _shellWatcher = new ShellRestartWatcher();
            _shellWatcher.ExplorerRestarted += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                // TakeOver 抛异常会 marshal 回 UI 线程到 DispatcherUnhandledException → crash → 反触发桌面空（I-6）。
                try
                {
                    _recoveryGuard.TakeOver(); _recoveryGuard.SetSelfCleanupOnExit(); /* 重新接管后重写 RunOnce（场景4） */
                    _host?.ReattachAll(); // M6：旧 WorkerW 已随 explorer 销毁 → 重启子进程 + 重挂新 WorkerW
                }
                catch (System.Exception ex)
                {
                    Log.Error(ex, "ExplorerRestarted TakeOver 失败");
                    try { _recoveryGuard.RestoreExplorer(); } catch { /* RestoreExplorer 也失败则不再升级 */ }
                }
            }));
            _shellWatcher.Attach(CreateMessageHookHwnd());

            // 5. M3-T6：拓扑变化（热插拔/分辨率/DPI/主屏切换）→ 防抖后 host 重建窗口集。
            _displayWatcher = new DisplayChangeWatcher();
            _displayWatcher.DisplayChanged += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                try { _host.RebuildToMatchTopology(); }
                catch (System.Exception ex)
                {
                    // 重建失败保现状（窗口集不动），下一次拓扑事件再试；不升级到崩 app。
                    Log.Error(ex, "RebuildToMatchTopology 失败（保现状）");
                }
            }));
            _displayWatcher.Attach();

            // 6. M4-T4：播放治理（全屏/电池/锁屏暂停壁纸）。
            _governor = new PlaybackGovernor(_host);

            
        }
        catch
        {
            // 回滚 HideIcons → 让 explorer 原生图标回来；rethrow 走 WPF 默认未处理流程。
            try { _recoveryGuard?.RestoreExplorer(); } catch { /* 已尽力 */ }
            throw;
        }
    }

    // M6：图标层已拆子进程，主进程用一个隐藏窗口承接 shell 消息（TaskbarCreated）。
    private Window? _messageHookWindow;

    private IntPtr CreateMessageHookHwnd()
    {
        _messageHookWindow = new Window { ShowInTaskbar = false, ShowActivated = false, Visibility = Visibility.Hidden };
        return new System.Windows.Interop.WindowInteropHelper(_messageHookWindow).EnsureHandle();
    }

    private void OnSettings_Clicked(object sender, RoutedEventArgs e)
    {
        // M5-T2：设置窗口单实例（已开则前置）。
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_host!);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnExit_Clicked(object sender, RoutedEventArgs e)
    {
        Log.Information("退出：托盘菜单点击");
        _tray?.Dispose();

        // MSIX 真机结论（2026-08-20 搜索证实）：Shutdown() 后 Dispatcher 关闭可能被阻塞，
        // OnExit 不保证执行 → 所有清理必须在 Shutdown() 之前同步完成。
        // 参考：react-native-windows#1470 / CefSharp#990 等打包应用同款问题。
        PerformExitCleanup();

        Shutdown();

        // 2s 兜底（清理已完成，这里只是杀掉可能的 Dispatcher 挂死空壳）
        var killer = new System.Threading.Timer(_ => Environment.Exit(0), null, 2000, System.Threading.Timeout.Infinite);
    }

    /// <summary>退出清理（保存→停子进程→恢复原生桌面→释放资源）。在 Shutdown() 之前调用。</summary>
    private void PerformExitCleanup()
    {
        try { _host?.SaveAllNow(); Log.Information("退出①：SaveAllNow 完成"); }
        catch (System.Exception ex) { Log.Error(ex, "退出 SaveAllNow 失败"); }

        _sync?.Dispose();
        _displayWatcher?.Dispose();
        _governor?.Dispose();
        _host?.CloseAll(); Log.Information("退出④：CloseAll 完成");
        try
        {
            _recoveryGuard?.RestoreExplorer();
            _recoveryGuard?.ClearSelfCleanup();
            Log.Information("退出⑤：原生桌面已恢复");
        }
        catch (System.Exception ex)
        {
            Log.Warning(ex, "退出 RestoreExplorer 失败，RunOnce 兜底保留");
        }
        _singleInstanceMutex?.Dispose();
        LogConfig.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 主体清理已在 PerformExitCleanup()（Shutdown 前同步完成，MSIX 安全）。
        // 这里是幂等兜底（各组件 Dispose 已做 no-op 保护）+ 防止 PerformExitCleanup 未跑的路径。
        try { _host?.SaveAllNow(); } catch { /* 幂等 */ }
        _host?.CloseAll();
        try { _recoveryGuard?.RestoreExplorer(); _recoveryGuard?.ClearSelfCleanup(); } catch { /* 幂等 */ }
        _tray?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
