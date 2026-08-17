using DesktopManager.Native;

namespace DesktopManager.App.Services;

/// <summary>M6 终态：顶层窗口形态（桌面层 SetParent 在本机物理输出失效，见 WindowInterop.AttachTopLevel 注释）。</summary>
public static class DesktopLayerHost
{
    public static void AttachToDesktop(long childHwnd, int monX, int monY, int monW, int monH, bool iconLayer = false)
    {
        WindowInterop.AttachTopLevel(new IntPtr(childHwnd), monX, monY, monW, monH, iconLayer);
    }

    public static void RepositionChild(long childHwnd, int monX, int monY, int monW, int monH)
    {
        WindowInterop.RepositionDesktopChild(new IntPtr(childHwnd), monX, monY, monW, monH);
    }
}
