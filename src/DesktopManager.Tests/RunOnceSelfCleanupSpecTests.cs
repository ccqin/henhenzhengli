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
    public void BuildRestoreCommand_UsesRegExeAndTargetsHideIcons()
    {
        var cmd = RunOnceSelfCleanupSpec.BuildRestoreCommand();

        // reg.exe（Windows 自带，登录时由 RunOnce 执行）
        Assert.StartsWith("reg.exe ", cmd);
        // 目标值名
        Assert.Contains("HideIcons", cmd);
        // 写入 Advanced 键路径
        Assert.Contains(RunOnceSelfCleanupSpec.AdvancedKeyPath, cmd);
        // 类型 REG_DWORD
        Assert.Contains("REG_DWORD", cmd);
        // 强制覆盖 /f
        Assert.Contains("/f", cmd);
        // 值 0
        Assert.Contains("/d 0", cmd);
        // add 子命令
        Assert.Contains(" add ", cmd);
        // /v 指定值名
        Assert.Contains("/v HideIcons", cmd);
    }

    [Fact]
    public void BuildRestoreCommand_IsDeterministicAndStable()
    {
        // 覆盖式重写每次启动调用，命令须稳定（便于每次启动幂等覆盖）
        var a = RunOnceSelfCleanupSpec.BuildRestoreCommand();
        var b = RunOnceSelfCleanupSpec.BuildRestoreCommand();
        Assert.Equal(a, b);
    }

    [Fact]
    public void BuildRestoreCommand_PathIsQuoted()
    {
        // 路径含空格（CurrentVersion 等），reg.exe 要求引号包裹
        var cmd = RunOnceSelfCleanupSpec.BuildRestoreCommand();
        Assert.Contains($"\"{RunOnceSelfCleanupSpec.AdvancedKeyPath}\"", cmd);
    }
}
