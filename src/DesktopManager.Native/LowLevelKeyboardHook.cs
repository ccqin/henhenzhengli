using System.Runtime.InteropServices;

namespace DesktopManager.Native;

/// <summary>低级键盘钩子（WH_KEYBOARD_LL）：仅观察、永不拦截（总是 CallNextHookEx）。
/// 用途：NOACTIVATE 的图标层窗口收不到键盘焦点，"桌面打字快速查找"靠它感知。
/// 实现：专职泵线程安装钩子并跑消息循环（LL 钩子回调经安装线程的消息泵分发）；
/// 回调在泵线程，调用方自行切 UI 调度。</summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_QUIT = 0x0012;

    private readonly Action<int, bool> _onKey;   // (vk, isKeyDown)
    private readonly HookProc _proc;
    private readonly Thread _pump;
    private IntPtr _hook;
    private readonly ManualResetEventSlim _installed = new(false);

    public LowLevelKeyboardHook(Action<int, bool> onKey)
    {
        _onKey = onKey;
        _proc = HookCallback;
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "KbdHookPump" };
        _pump.Start();
        if (!_installed.Wait(3000)) throw new InvalidOperationException("键盘钩子安装超时");
    }

    private void PumpLoop()
    {
        _tid = GetCurrentThreadId();
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        _installed.Set();
        if (_hook == IntPtr.Zero) return;
        while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            uint msg = (uint)wParam.ToInt64();
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            try { _onKey(Marshal.ReadInt32(lParam) & 0xFF, down); } catch { }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        PostThreadMessageW(_tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    private uint _tid;   // 泵线程 id（PumpLoop 开头记录；Dispose 向它投 WM_QUIT 结束消息循环）

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX, ptY; }
}

/// <summary>Win32 杂项（图标层键盘查找的前台判断用）。</summary>
public static class Win32
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int max);
}
