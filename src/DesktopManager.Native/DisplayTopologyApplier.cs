using System.Runtime.InteropServices;
using System.Text;

namespace DesktopManager.Native;

/// <summary>M5-T5（修订）：显示拓扑诊断。
/// 拖拽改拓扑功能已按用户决策移除（本机 Intel MTL DWM 虚拟显示栈拒绝第三方拓扑变更：
/// legacy CDS 与 CCD SetDisplayConfig 穷举全灭，提权无效；Windows Settings 走内部 broker）。
/// 保留 GDI 设备枚举诊断（--debug-monitors 用）。</summary>
public static class DisplayTopologyApplier
{
    private const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOQuality;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    /// <summary>诊断：枚举 GDI 设备 + 当前位置/分辨率读取情况。</summary>
    public static string Diagnose()
    {
        var sb = new StringBuilder();
        for (uint i = 0; i < 8; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            bool ok = EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm);
            sb.Append($"dev[{i}] {dd.DeviceName} flags=0x{dd.StateFlags:X} enumOk={ok} pos=({dm.dmPositionX},{dm.dmPositionY}) res={dm.dmPelsWidth}x{dm.dmPelsHeight} | ");
        }
        return sb.ToString();
    }
}
