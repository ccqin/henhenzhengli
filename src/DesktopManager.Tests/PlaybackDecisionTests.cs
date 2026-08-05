using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

/// <summary>M4-T1：播放暂停决策真值表（全屏应用/电池/锁屏 → 暂停，安全方向）。</summary>
public class PlaybackDecisionTests
{
    [Theory]
    [InlineData(false, false, false, false)] // 正常：播放
    [InlineData(true,  false, false, true)]  // 全屏应用 → 暂停
    [InlineData(false, true,  false, true)]  // 电池 → 暂停
    [InlineData(false, false, true,  true)]  // 锁屏 → 暂停
    [InlineData(true,  true,  false, true)]
    [InlineData(true,  false, true,  true)]
    [InlineData(false, true,  true,  true)]
    [InlineData(true,  true,  true,  true)]
    public void ShouldPause_TruthTable(bool fullScreen, bool battery, bool locked, bool expected)
        => Assert.Equal(expected, PlaybackDecision.ShouldPause(fullScreen, battery, locked));
}

/// <summary>M4-T4：全屏覆盖判定（最大化 ≠ 全屏）。</summary>
public class MonitorCoverageTests
{
    // 显示器 (0,0)-(1920,1080)
    [Fact]
    public void ExactCover_IsFullScreen()
        => Assert.True(MonitorCoverage.Covers(0, 0, 1920, 1080, 0, 0, 1920, 1080));

    [Fact]
    public void LargerThanMonitor_IsFullScreen()
        => Assert.True(MonitorCoverage.Covers(-8, -8, 1928, 1088, 0, 0, 1920, 1080));

    [Fact]
    public void MaximizedWindow_WorkArea_NotFullScreen()
        // 最大化 = 工作区（任务栏 48px 不在内）→ 不覆盖
        => Assert.False(MonitorCoverage.Covers(0, 0, 1920, 1032, 0, 0, 1920, 1080));

    [Fact]
    public void SmallerWindow_NotFullScreen()
        => Assert.False(MonitorCoverage.Covers(100, 100, 900, 700, 0, 0, 1920, 1080));

    [Fact]
    public void SecondaryMonitor_Cover()
        => Assert.True(MonitorCoverage.Covers(-1920, 0, 0, 1080, -1920, 0, 0, 1080));
}
