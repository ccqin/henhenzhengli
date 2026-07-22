using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

/// <summary>
/// I-3 自清理（RunOnce 兜底）的纯数据/命令字符串单测。
/// 注册表实际读写不测（污染系统），只测可测的常量格式与命令构造逻辑。
/// </summary>
public class RunOnceSelfCleanupSpecTests
{
    [Fact]
    public void RunOnceKeyPath_IsHkcuRunOnce()
    {
        // HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce（不含 HKCU 前缀，HKCU 为 Registry.CurrentUser 基根）
        Assert.EndsWith(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", RunOnceSelfCleanupSpec.RunOnceKeyPath);
    }

    [Fact]
    public void ValueName_IsDmRestoreIcons()
    {
        Assert.Equal("DM_RestoreIcons", RunOnceSelfCleanupSpec.ValueName);
    }

    [Fact]
    public void AdvancedKeyPath_MatchesExplorerAdvanced()
    {
        // 必须含 HKCU 根键前缀：reg.exe CLI 要求完整路径，无前缀则命令失败（I-3 兜底失效→桌面永久空，致命）。
        Assert.StartsWith(@"HKCU\", RunOnceSelfCleanupSpec.AdvancedKeyPath);
        Assert.EndsWith(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
            RunOnceSelfCleanupSpec.AdvancedKeyPath);
    }

    [Fact]
    public void HideIconsValueName_IsHideIcons()
    {
        Assert.Equal("HideIcons", RunOnceSelfCleanupSpec.HideIconsValueName);
    }

    [Fact]
    public void HideIconsRestoreValue_IsZero()
    {
        // 兜底语义：恢复 HideIcons=0（显示桌面图标）
        Assert.Equal(0, RunOnceSelfCleanupSpec.HideIconsRestoreValue);
    }

    [Fact]
    public void BuildRestoreCommand_Produces_Quoted_AppPath_With_RestoreIcons_Flag()
    {
        var cmd = RunOnceSelfCleanupSpec.BuildRestoreCommand(@"C:\Program Files\DesktopManager\app.exe");

        // C1：RunOnce 值现在是启动 app --restore-icons 模式（含 WM_SETTINGCHANGE 广播），而非 reg.exe。
        // 字面量断言（非自引用）确保 --restore-icons 标志真的在命令里——
        // 缺了它 app 会走正常接管路径而非恢复后退出 → I-3 致命项时序依赖未消除。
        Assert.Contains("--restore-icons", cmd);
        // 路径必须用引号包裹（含空格路径如 Program Files 不引用会被 RunOnce 解析截断）
        Assert.Contains("\"", cmd);
    }

    [Fact]
    public void BuildRestoreCommand_QuotesAppPathWithSpaces()
    {
        // 含空格路径必须出现在引号内（"C:\Program Files\DM\app.exe"），否则 RunOnce 启动时路径被截断
        var cmd = RunOnceSelfCleanupSpec.BuildRestoreCommand(@"C:\Program Files\DM\app.exe");
        Assert.Contains("\"C:\\Program Files\\DM\\app.exe\"", cmd);
    }

    [Fact]
    public void BuildRestoreCommand_StartsWith_Quoted_AppPath_Followed_By_Flag()
    {
        // 完整格式："<appPath>" --restore-icons
        var cmd = RunOnceSelfCleanupSpec.BuildRestoreCommand(@"D:\tools\app.exe");
        Assert.StartsWith("\"D:\\tools\\app.exe\" --restore-icons", cmd);
    }

    [Fact]
    public void BuildRestoreCommand_IsDeterministicAndStable()
    {
        // 覆盖式重写每次启动调用，同路径命令须稳定（便于每次启动幂等覆盖）
        var a = RunOnceSelfCleanupSpec.BuildRestoreCommand(@"C:\app.exe");
        var b = RunOnceSelfCleanupSpec.BuildRestoreCommand(@"C:\app.exe");
        Assert.Equal(a, b);
    }
}
