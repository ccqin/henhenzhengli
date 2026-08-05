using System.Runtime.InteropServices;

namespace DesktopManager.Native;

/// <summary>M4-T4：电源状态（GetSystemPowerStatus）。ACLineStatus=0 电池 / 1 交流 / 255 未知。
/// 未知按非电池处理（不误暂停桌面壁纸）。</summary>
public static class PowerStatus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static bool IsOnBattery()
    {
        try
        {
            return GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;
        }
        catch
        {
            return false; // 查询失败 ≠ 电池，不误暂停
        }
    }
}
