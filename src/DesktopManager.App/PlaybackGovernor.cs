using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopManager.Core.Services;
using DesktopManager.Native;
using Microsoft.Win32;
using Serilog;

namespace DesktopManager.App;

/// <summary>M4-T4：播放治理——全屏应用/电池/锁屏时暂停所有壁纸播放，恢复后继续。
/// 输入采集：前台窗口轮询 1.5s（全屏判定）+ <see cref="SystemEvents.PowerModeChanged"/>（AC/DC）
/// + <see cref="SystemEvents.SessionSwitch"/>（锁屏/解锁）。决策走 Core 纯函数
/// <see cref="PlaybackDecision.ShouldPause"/>（单测覆盖）。暂停/恢复经 host 幂等下发。</summary>
public sealed class PlaybackGovernor : IDisposable
{
    private readonly MultiMonitorHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private bool _locked;
    private bool _paused;

    // shell/桌面类窗口不算「全屏应用」（它们覆盖整屏是本职工作）。
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "DV2ControlHost"
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private readonly PowerModeChangedEventHandler _onPower;
    private readonly SessionSwitchEventHandler _onSession;

    public PlaybackGovernor(MultiMonitorHost host)
    {
        _host = host;
        _dispatcher = Application.Current.Dispatcher;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => Evaluate();
        _timer.Start();

        // 电源/会话事件在非 UI 线程触发 → 回 UI 线程评估。句柄存字段供 Dispose 退订。
        _onPower = (_, _) => _dispatcher.BeginInvoke(new Action(Evaluate));
        _onSession = (_, e) => _dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.Reason == SessionSwitchReason.SessionLock) _locked = true;
            else if (e.Reason == SessionSwitchReason.SessionUnlock) _locked = false;
            else return;
            Evaluate();
        }));
        SystemEvents.PowerModeChanged += _onPower;
        SystemEvents.SessionSwitch += _onSession;
    }

    private void Evaluate()
    {
        bool full = DetectFullScreenApp();
        bool battery = PowerStatus.IsOnBattery();
        bool pause = PlaybackDecision.ShouldPause(full, battery, _locked);
        if (pause == _paused) return;
        _paused = pause;
        if (pause) _host.PauseAllWallpapers();
        else _host.ResumeAllWallpapers();
        Log.Information("PlaybackGovernor：{State}（fullscreen={F} battery={B} locked={L}）",
            pause ? "暂停" : "恢复", full, battery, _locked);
    }

    /// <summary>前台窗口是否覆盖任一显示器整屏（最大化 ≠ 全屏：工作区不含任务栏条）。</summary>
    private bool DetectFullScreenApp()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == Environment.ProcessId) return false; // 自己的窗口不算
        var cls = new StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (ExcludedClasses.Contains(cls.ToString())) return false;
        if (!GetWindowRect(fg, out var r)) return false;

        foreach (var m in MonitorEnumerator.Enumerate())
        {
            if (MonitorCoverage.Covers(r.Left, r.Top, r.Right, r.Bottom,
                    m.X, m.Y, m.X + m.Width, m.Y + m.Height))
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        _timer.Stop();
        SystemEvents.PowerModeChanged -= _onPower;
        SystemEvents.SessionSwitch -= _onSession;
    }
}
