using System.Diagnostics;
using Serilog;

namespace DesktopManager.Native;

/// <summary>
/// 强制重启 explorer 进程（I-3 <c>--restore-icons</c> 崩溃恢复专用）。
///
/// <para><b>背景（I-3 真机根因）</b>：app 接管（HideIcons=1）后崩溃/被强杀 → OnExit 未跑 →
/// 下次登录 Windows 执行 RunOnce → 启动 app <c>--restore-icons</c> → <c>RestoreExplorer</c>
/// 写 HideIcons=0 + WM_SETTINGCHANGE 广播。但登录时 explorer 刚启动尚未就绪，广播被时序吞 →
/// HideIcons 已写 0 但 explorer 未刷新显示 → 桌面图标一直空（真机已确认）。</para>
///
/// <para><b>解法</b>：<see cref="Restart"/> 杀 explorer 进程 → Windows AutoRestartShell（默认开启，
/// HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\AutoRestartShell=1）自动重启 explorer.exe
/// → 新 explorer 进程启动时重新读取 HideIcons=0 → 桌面图标显示。<b>不靠广播，重读注册表，消除时序赌博。</b></para>
///
/// <para><b>范围</b>：仅在 <c>--restore-icons</c> 模式调用；正常启动/退出不走此路径
/// （Kill explorer 会闪一下任务栏，disruptive，仅崩溃恢复值得）。</para>
/// </summary>
public static class ExplorerRestarter
{
    /// <summary>杀 explorer 后等待 AutoRestartShell 重启的上限（ms）。超时则兜底主动 Start。</summary>
    /// <remarks>登录时（RunOnce 紧接登录）1.5s 可接受：任务栏闪一下，优于桌面永久空。</remarks>
    private const int AutoRestartWaitMs = 1500;

    /// <summary>
    /// 杀 explorer 进程并等待 Windows AutoRestartShell 重启；超时则兜底主动 Start explorer.exe。
    /// 全程 try/catch 兜底，绝不抛出（不阻塞 <c>--restore-icons</c> 模式的 Shutdown）。
    /// </summary>
    public static void Restart()
    {
        try
        {
            // 1. 杀 explorer（若在跑）。Process.GetProcessesByName + Kill 优于 taskkill：无外部进程依赖、可控、可逐 PID 记录。
            var explorers = Process.GetProcessesByName("explorer");
            if (explorers.Length > 0)
            {
                foreach (var p in explorers)
                {
                    try { p.Kill(); }
                    catch (Exception ex) { Log.Warning(ex, "Kill explorer (PID={Pid}) 失败", p.Id); }
                    p.Dispose();
                }
                Log.Information("重启 explorer 强制刷新桌面：已 Kill {Count} 个 explorer 进程，等待 AutoRestartShell 重启", explorers.Length);
            }
            else
            {
                // 无 explorer 在跑（登录时已崩或未拉起）：直接走兜底 Start 路径。
                Log.Information("重启 explorer 强制刷新桌面：当前无 explorer 进程，走兜底 Start 路径");
            }

            // 2. 等 AutoRestartShell 自动重启 explorer（新进程启动时重读 HideIcons=0）。
            Thread.Sleep(AutoRestartWaitMs);

            // 3. 兜底：AutoRestartShell 被禁（罕见）或未及时拉起 → 主动 Start explorer.exe（= 拉起 shell，非开窗）。
            //    explorer.exe 无参启动且当前无 shell 进程时，会作为 shell（桌面+任务栏+托盘）启动。
            if (Process.GetProcessesByName("explorer").Length == 0)
            {
                Log.Warning("AutoRestartShell 未在 {Ms}ms 内重启 explorer，兜底主动 Start explorer.exe", AutoRestartWaitMs);
                var explorerPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                Process.Start(new ProcessStartInfo(explorerPath) { UseShellExecute = false });
            }
        }
        catch (Exception ex)
        {
            // 最坏情况：桌面仍空（用户手动启动 explorer 或重启系统）。不阻塞 --restore-icons 的 Shutdown。
            Log.Error(ex, "Restart explorer 异常（不阻塞 --restore-icons 退出）");
        }
    }
}
