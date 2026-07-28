using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopManager.Native;

/// <summary>窗口样式 P/Invoke 封装：供 WallpaperWindow / IconLayerWindow 共用置底、点击穿透、不抢焦点等行为。</summary>
public static class WindowInterop
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    // 64-bit safe variants (x64 用 PtrW，x86 用 W)
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetCurrentThreadId();

    public static long GetExtendedStyle(IntPtr hWnd)
        => (IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, GWL_EXSTYLE) : new IntPtr(GetWindowLong32(hWnd, GWL_EXSTYLE))).ToInt64();

    public static void SetExtendedStyle(IntPtr hWnd, long value)
    {
        // SetWindowLong(Ptr) 成功时不清 last-error slot，必须用「返回值为 0 且 error 非 0」双条件判失败，
        // 否则 WPF 内部调用的残留错误码会让成功路径误抛 Win32Exception。返回 0 也可能是合法（前值就是 0）。
        int err;
        if (IntPtr.Size == 8)
        {
            IntPtr prev = SetWindowLongPtr64(hWnd, GWL_EXSTYLE, new IntPtr(value));
            err = prev == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        }
        else
        {
            int prev = SetWindowLong32(hWnd, GWL_EXSTYLE, (int)value);
            err = prev == 0 ? Marshal.GetLastWin32Error() : 0;
        }
        if (err != 0) throw new Win32Exception(err);
    }

    public static void SendToBottom(IntPtr hWnd)
    {
        if (!SetWindowPos(hWnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>壁纸层：点击穿透（WS_EX_LAYERED|TRANSPARENT|NOACTIVATE）+ 置底。</summary>
    public static void MakeClickThrough(IntPtr hWnd)
    {
        long ex = GetExtendedStyle(hWnd);
        SetExtendedStyle(hWnd, ex | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        SendToBottom(hWnd);
    }

    /// <summary>图标层：不点击穿透但同样不抢焦点（WS_EX_LAYERED|NOACTIVATE，去 TRANSPARENT）。M1 IconLayerWindow 用。</summary>
    public static void MakeNonInteractiveTopmost(IntPtr hWnd)
    {
        long ex = GetExtendedStyle(hWnd);
        SetExtendedStyle(hWnd, ex | WS_EX_LAYERED | WS_EX_NOACTIVATE);
        SendToBottom(hWnd);
    }

    // ---------- M2 真机修复 Bug 2：NOACTIVATE 临时激活 ----------
    // 背景：IconLayerWindow 设 WS_EX_NOACTIVATE（桌面图标层不抢 explorer 焦点）→ app 进程从不获取前台
    // → 所有 TextBox（FenceControl.TitleEdit / RenameDialog.Input）无法接收键盘输入。
    // 不能直接去 NOACTIVATE（全屏透明窗口会浮到所有窗口前覆盖一切）。方案：仅在需要键盘输入时
    // 临时去 NOACTIVATE + SetForegroundWindow；输入结束恢复 NOACTIVATE + SendToBottom 回桌面层 Z-order。
    // 调用方必须保证 EnableActivation / RestoreNonInteractive 严格成对（try/finally），否则 IconLayer
    // 激活后回不到桌面层（浮在所有窗口前）。

    /// <summary>去掉 WS_EX_NOACTIVATE 并尝试把窗口设到前台（让 app 进程获得输入焦点）。
    /// 返回之前的 ex style（含 NOACTIVATE），供 RestoreNonInteractive 恢复。
    /// 内部用 AttachThreadInput 兜底：NOACTIVATE 进程调 SetForegroundWindow 常被前台锁定拒绝，
    /// AttachThreadInput 把当前前台线程的 input 队列临时挂到本线程，绕过 SetForegroundWindow 的前厣检查。</summary>
    public static long EnableActivation(IntPtr hWnd)
    {
        long prev = GetExtendedStyle(hWnd);
        if ((prev & WS_EX_NOACTIVATE) != 0)
        {
            SetExtendedStyle(hWnd, prev & ~WS_EX_NOACTIVATE);
        }
        ForceForeground(hWnd);
        return prev;
    }

    /// <summary>恢复 NOACTIVATE（用 EnableActivation 返回的 prevEx）并 SendToBottom 回桌面层 Z-order。
    /// 必须 EnableActivation/RestoreNonInteractive 成对调用（try/finally 包裹）。prevEx 含原 NOACTIVATE。</summary>
    public static void RestoreNonInteractive(IntPtr hWnd, long prevEx)
    {
        // prevEx 是 EnableActivation 抓取的快照（含 NOACTIVATE），直接 set 回去最安全。
        // 多次调用幂等：当前 ex 已等于 prevEx 时 SetExtendedStyle 不会改变行为（Win32 Set 调用）。
        try
        {
            SetExtendedStyle(hWnd, prevEx);
        }
        catch (Win32Exception)
        {
            // 极端场景（窗口已关闭等）不应阻塞调用方 finally。降级：仅尝试重 |NOACTIVATE，再失败就忽略。
            try { SetExtendedStyle(hWnd, GetExtendedStyle(hWnd) | WS_EX_NOACTIVATE); }
            catch { /* 窗口已无效，无法恢复，静默 */ }
        }
        try { SendToBottom(hWnd); }
        catch (Win32Exception) { /* 同上：不阻塞 finally */ }
    }

    /// <summary>让 hWnd 进入前台。AttachThreadInput trick：把当前前台窗口的线程 input 队列 attach 到本线程，
    /// 使 SetForegroundWindow 通过（绕过「非前台进程不能 SetForeground」限制），随后立即 detach。</summary>
    private static void ForceForeground(IntPtr hWnd)
    {
        var fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint curThread = GetCurrentThreadId();
        bool attached = false;
        if (fgThread != 0 && fgThread != curThread)
        {
            attached = AttachThreadInput(curThread, fgThread, true);
        }
        try
        {
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached) AttachThreadInput(curThread, fgThread, false);
        }
    }
}
