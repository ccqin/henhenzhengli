using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DesktopManager.App;

/// <summary>监听 explorer.exe 重启（TaskbarCreated 广播），触发重新接管。</summary>
public sealed class ShellRestartWatcher
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    private readonly uint _taskbarCreated;

    public event Action? ExplorerRestarted;

    public ShellRestartWatcher() => _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

    public void Attach(IntPtr hwnd)
    {
        var src = HwndSource.FromHwnd(hwnd);
        if (src == null) return;
        src.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == _taskbarCreated) ExplorerRestarted?.Invoke();
        return IntPtr.Zero;
    }
}
