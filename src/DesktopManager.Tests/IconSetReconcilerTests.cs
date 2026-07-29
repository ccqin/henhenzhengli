using DesktopManager.Core.Models;
using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

/// <summary>P0-T3：IconSetReconciler 纯逻辑契约。
/// PlanSnapshot（启动/explorer 重启全量对账）+ PlanDiff（sync.Changed 增量对账）。
/// 匹配键：FilePath（OrdinalIgnoreCase，与 DesktopDiff 一致）。</summary>
public class IconSetReconcilerTests
{
    private static IconItem I(string name, double x = 0, double y = 0)
        => new("C:\\" + name, name, x, y);

    private static HashSet<string> Fenced(params string[] names)
        => new(names.Select(n => "C:\\" + n), StringComparer.OrdinalIgnoreCase);

    // ---------- PlanSnapshot ----------

    [Fact]
    public void PlanSnapshot_EmptyAll_EmptyLoose_ReturnsEmpty()
    {
        var (toAdd, toRemove) = IconSetReconciler.PlanSnapshot(
            Array.Empty<IconItem>(), Fenced(), Array.Empty<IconItem>());
        Assert.Empty(toAdd);
        Assert.Empty(toRemove);
    }

    [Fact]
    public void PlanSnapshot_NewItems_NotFenced_NotInLoose_GoToAdd()
    {
        var all = new[] { I("a.txt"), I("b.txt") };
        var (toAdd, toRemove) = IconSetReconciler.PlanSnapshot(all, Fenced(), Array.Empty<IconItem>());
        Assert.Equal(new[] { "a.txt", "b.txt" }, toAdd.Select(i => i.DisplayName));
        Assert.Empty(toRemove);
    }

    [Fact]
    public void PlanSnapshot_FencedItems_NotInToAdd()
    {
        var all = new[] { I("a.txt"), I("b.txt") };
        var fenced = Fenced("a.txt");
        var (toAdd, _) = IconSetReconciler.PlanSnapshot(all, fenced, Array.Empty<IconItem>());
        Assert.Equal(new[] { "b.txt" }, toAdd.Select(i => i.DisplayName));
    }

    [Fact]
    public void PlanSnapshot_ItemsAlreadyInLoose_NotReAdded()
    {
        var all = new[] { I("a.txt"), I("b.txt") };
        var loose = new[] { I("a.txt") }; // a 已在散落区
        var (toAdd, toRemove) = IconSetReconciler.PlanSnapshot(all, Fenced(), loose);
        Assert.Equal(new[] { "b.txt" }, toAdd.Select(i => i.DisplayName));
        Assert.Empty(toRemove); // a 既在 all 也在 loose，不删
    }

    [Fact]
    public void PlanSnapshot_LooseNotInAll_GoToRemove()
    {
        var all = new[] { I("a.txt") };
        var loose = new[] { I("a.txt"), I("b.txt") }; // b 已不在 all
        var (toAdd, toRemove) = IconSetReconciler.PlanSnapshot(all, Fenced(), loose);
        Assert.Empty(toAdd);
        Assert.Equal(new[] { "b.txt" }, toRemove.Select(i => i.DisplayName));
    }

    [Fact]
    public void PlanSnapshot_LooseNowFenced_GoToRemove()
    {
        var all = new[] { I("a.txt"), I("b.txt") };
        var fenced = Fenced("b.txt");
        var loose = new[] { I("a.txt"), I("b.txt") }; // b 现已 fenced
        var (toAdd, toRemove) = IconSetReconciler.PlanSnapshot(all, fenced, loose);
        Assert.Empty(toAdd);
        Assert.Equal(new[] { "b.txt" }, toRemove.Select(i => i.DisplayName));
    }

    [Fact]
    public void PlanSnapshot_Idempotent_SameInputSameOutput()
    {
        var all = new[] { I("a.txt"), I("b.txt"), I("c.txt") };
        var fenced = Fenced("c.txt");
        var loose = new[] { I("a.txt") };
        var r1 = IconSetReconciler.PlanSnapshot(all, fenced, loose);
        var r2 = IconSetReconciler.PlanSnapshot(all, fenced, loose);
        Assert.Equal(r1.toAdd.Select(i => i.DisplayName), r2.toAdd.Select(i => i.DisplayName));
        Assert.Equal(r1.toRemove.Select(i => i.DisplayName), r2.toRemove.Select(i => i.DisplayName));
    }

    // ---------- PlanDiff ----------

    [Fact]
    public void PlanDiff_AddedNotFenced_GoToAdd()
    {
        var diff = new DesktopDiff(new[] { I("a.txt") }, Array.Empty<IconItem>());
        var (toAdd, toRemove) = IconSetReconciler.PlanDiff(diff, Fenced(), Array.Empty<IconItem>());
        Assert.Equal(new[] { "a.txt" }, toAdd.Select(i => i.DisplayName));
        Assert.Empty(toRemove);
    }

    [Fact]
    public void PlanDiff_AddedFenced_NotInToAdd()
    {
        // Added 的文件已属 Fence（如刚被拖入 Fence 的文件又被 sync 当作新增）→ 散落区不管。
        var diff = new DesktopDiff(new[] { I("a.txt") }, Array.Empty<IconItem>());
        var fenced = Fenced("a.txt");
        var (toAdd, _) = IconSetReconciler.PlanDiff(diff, fenced, Array.Empty<IconItem>());
        Assert.Empty(toAdd);
    }

    [Fact]
    public void PlanDiff_Removed_PresentInLoose_GoToRemove()
    {
        var diff = new DesktopDiff(Array.Empty<IconItem>(), new[] { I("a.txt") });
        var loose = new[] { I("a.txt") };
        var (toAdd, toRemove) = IconSetReconciler.PlanDiff(diff, Fenced(), loose);
        Assert.Empty(toAdd);
        Assert.Equal(new[] { "C:\\a.txt" }, toRemove);
    }

    [Fact]
    public void PlanDiff_Removed_NotInLoose_NotInToRemove()
    {
        // Removed 的文件不在散落区（如原本就 fenced）→ 无需对 _looseIcons 操作。
        var diff = new DesktopDiff(Array.Empty<IconItem>(), new[] { I("a.txt") });
        var (_, toRemove) = IconSetReconciler.PlanDiff(diff, Fenced(), Array.Empty<IconItem>());
        Assert.Empty(toRemove);
    }

    [Fact]
    public void PlanDiff_Rename_OldRemoved_NewAdded_BothHandled()
    {
        // R9：rename = 旧 path Removed + 新 path Added，两端各自处理。
        var diff = new DesktopDiff(
            new[] { I("new.txt") },
            new[] { I("old.txt") });
        var loose = new[] { I("old.txt") }; // 旧名当前在散落区
        var (toAdd, toRemove) = IconSetReconciler.PlanDiff(diff, Fenced(), loose);
        Assert.Equal(new[] { "new.txt" }, toAdd.Select(i => i.DisplayName));
        Assert.Equal(new[] { "C:\\old.txt" }, toRemove);
    }

    [Fact]
    public void PlanDiff_NewItemsHaveZeroXY_ForGridPositioning()
    {
        var diff = new DesktopDiff(new[] { I("a.txt") }, Array.Empty<IconItem>());
        var (toAdd, _) = IconSetReconciler.PlanDiff(diff, Fenced(), Array.Empty<IconItem>());
        Assert.All(toAdd, i => { Assert.Equal(0, i.X); Assert.Equal(0, i.Y); });
    }

    [Fact]
    public void PlanDiff_Idempotent_SameInputSameOutput()
    {
        var diff = new DesktopDiff(new[] { I("new.txt") }, new[] { I("old.txt") });
        var loose = new[] { I("old.txt") };
        var r1 = IconSetReconciler.PlanDiff(diff, Fenced(), loose);
        var r2 = IconSetReconciler.PlanDiff(diff, Fenced(), loose);
        Assert.Equal(r1.toAdd.Select(i => i.DisplayName), r2.toAdd.Select(i => i.DisplayName));
        Assert.Equal(r1.toRemove, r2.toRemove);
    }
}
