using System.Runtime.InteropServices;

namespace DesktopManager.Native;

/// <summary>单个显示器运行期信息（M3-T1 扩展）。
/// <para><see cref="DeviceName"/>：GDI 设备名（\\.\DISPLAYn），**不持久**（插拔/换顺序会变），仅运行期定位用。</para>
/// <para><see cref="PersistentId"/>：持久设备路径（含 EDID 硬件标识，插拔/换顺序不变），布局归属唯一键。
/// QueryDisplayConfig 不可用时退化为 DeviceName（宁可串屏不崩，见 MonitorIdResolver）。</para>
/// <para>X/Y/Width/Height 为全分辨率矩形；Work* 为工作区（排除任务栏），图标层窗口按工作区定位。</para></summary>
public record MonitorInfo(
    string DeviceName,
    string PersistentId,
    int X, int Y, int Width, int Height,
    int WorkX, int WorkY, int WorkWidth, int WorkHeight,
    bool IsPrimary);

public static class MonitorEnumerator
{
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
        EnumMonitorsDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    private delegate bool EnumMonitorsDelegate(IntPtr hMonitor, IntPtr hdcMonitor,
        ref RECT lprcMonitor, IntPtr dwData);

    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        // M3-T1：GDI 名 → 持久 ID 映射（一次解析，枚举全程复用）。
        // QueryDisplayConfig 失败 → 空字典 → 退化 GDI 名（MonitorIdResolver 契约）。
        var idMap = MonitorIdResolver.ResolveGdiNameToPersistentId();

        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (hMon, _hdc, ref rc, _data) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMon, ref mi))
                {
                    var deviceName = mi.szDevice;
                    var persistentId = idMap.TryGetValue(deviceName, out var pid) ? pid : deviceName;
                    list.Add(new MonitorInfo(
                        deviceName,
                        persistentId,
                        rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top,
                        mi.rcWork.Left, mi.rcWork.Top,
                        mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top,
                        (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
                }
                return true;
            }, IntPtr.Zero);
        return list;
    }
}
