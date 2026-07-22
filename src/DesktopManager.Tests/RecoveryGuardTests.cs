using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

public class RecoveryGuardTests
{
    [Theory]
    [InlineData(true, RecoveryState.PreviouslyTakenOver)]
    [InlineData(false, RecoveryState.Clean)]
    public void Detect_ReflectsHideIcons(bool hideIcons, RecoveryState expected)
    {
        var detector = new RecoveryStateDetector(() => hideIcons, _ => { });
        Assert.Equal(expected, detector.Detect());
    }

    [Fact]
    public void TakeOver_SetsHideIconsTrue()
    {
        bool set = false;
        var detector = new RecoveryStateDetector(() => false, v => set = v);
        detector.TakeOver();
        Assert.True(set);
    }

    [Fact]
    public void RestoreExplorer_SetsHideIconsFalse()
    {
        bool set = true;
        var detector = new RecoveryStateDetector(() => true, v => set = v);
        detector.RestoreExplorer();
        Assert.False(set);
    }
}
