using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopManager.App.Windows;

public partial class WallpaperWindow : Window
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

    private static long GetExStyle(IntPtr hWnd)
        => (IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, GWL_EXSTYLE) : new IntPtr(GetWindowLong32(hWnd, GWL_EXSTYLE))).ToInt64();

    private static void SetExStyle(IntPtr hWnd, long value)
    {
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(hWnd, GWL_EXSTYLE, new IntPtr(value));
        else
            SetWindowLong32(hWnd, GWL_EXSTYLE, (int)value);
        int err = Marshal.GetLastWin32Error();
        if (err != 0) throw new Win32Exception(err);
    }

    public WallpaperWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetExStyle(hwnd);
            SetExStyle(hwnd, ex | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
            SendToBottom(hwnd);
        };
    }

    public void SendToBottom(IntPtr hwnd)
    {
        if (!SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
