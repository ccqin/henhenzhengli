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
}
