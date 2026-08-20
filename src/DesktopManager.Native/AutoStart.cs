using Microsoft.Win32;

namespace DesktopManager.Native;

/// <summary>开机自启动（HKCU\Software\Microsoft\Windows\CurrentVersion\Run，当前用户级，无需管理员）。</summary>
public static class AutoStart
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopManager";

    /// <summary>MSIX 环境检测（打包后才有 Package.Current；桌面版调用会抛）。</summary>
    private static bool IsMsix()
    {
        try
        {
            _ = Windows.ApplicationModel.Package.Current;
            return true;
        }
        catch { return false; }
    }

    /// <summary>MSIX StartupTask（需 manifest 声明 desktop:StartupTask 扩展；未声明时 API 抛/无效）。</summary>
    private static async Task<bool?> MsixGetAsync()
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync("DesktopManagerStart");
            return task.State switch
            {
                Windows.ApplicationModel.StartupTaskState.Enabled => true,
                Windows.ApplicationModel.StartupTaskState.Disabled => false,
                _ => null,
            };
        }
        catch { return null; }
    }

    private static async Task MsixSetAsync(bool enabled)
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync("DesktopManagerStart");
            if (enabled) await task.RequestEnableAsync();
            else task.Disable();
        }
        catch { /* manifest 未声明扩展等 */ }
    }

    public static bool IsEnabled()
    {
        if (IsMsix())
            return MsixGetAsync().GetAwaiter().GetResult() == true;
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) is string v && v.Length > 0;
    }

    public static void SetEnabled(bool enabled)
    {
        if (IsMsix())
        {
            MsixSetAsync(enabled).GetAwaiter().GetResult();
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
