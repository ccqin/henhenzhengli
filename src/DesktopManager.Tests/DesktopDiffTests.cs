using DesktopManager.Core.Models;
using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

public class DesktopDiffTests
{
    private static IconItem I(string name) => new("C:\\" + name, name);

    [Fact]
    public void Diff_NoChange_ReturnsEmpty()
    {
        var prev = new[] { I("a.txt"), I("b.txt") };
        var diff = DesktopDiff.Diff(prev, prev);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void Diff_AddedAndRemoved()
    {
        var prev = new[] { I("a.txt"), I("b.txt") };
        var cur  = new[] { I("b.txt"), I("c.txt") }; // 删 a，加 c
        var diff = DesktopDiff.Diff(prev, cur);
        Assert.Equal(new[] { "c.txt" }, diff.Added.Select(i => i.DisplayName));
        Assert.Equal(new[] { "a.txt" }, diff.Removed.Select(i => i.DisplayName));
    }
}
