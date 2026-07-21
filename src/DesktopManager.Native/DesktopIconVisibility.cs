using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopManager.Native;

/// <summary>隐藏/恢复 explorer 原生桌面图标显示（等价于桌面右键→查看→显示桌面图标）。</summary>
public static class DesktopIconVisibility
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ValueName = "HideIcons";

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, UIntPtr wParam, string? lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    private const uint WM_SETTINGCHANGE = 0x001A;
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    public static void HideDesktopIcons() => SetHidden(true);
    public static void ShowDesktopIcons() => SetHidden(false);

    private static void SetHidden(bool hidden)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AdvancedKey, writable: true);
        key.SetValue(ValueName, hidden ? 1 : 0, RegistryValueKind.DWord);
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero,
            "Shell", SMTO_ABORTIFHUNG, 1000, out _);
    }
}
