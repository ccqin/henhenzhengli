using System.Collections.Generic;
using DesktopManager.Core.Models;
using DesktopManager.Core.Services;
using Xunit;

namespace DesktopManager.Tests;

/// <summary>M6 编排逻辑下沉的单测：图标路由 / 壁纸解析 / 拓扑重建孤儿计算（原 MultiMonitorHost 内联逻辑）。</summary>
public class OrchestrationTests
{
    // ---------- IconRouter ----------

    private static readonly IReadOnlyDictionary<string, string> EmptyHints = new Dictionary<string, string>();

    [Fact]
    public void IconRouter_OwnerFirst_ThenHint_ThenPrimary()
    {
        var owner = new Dictionary<string, string> { ["a.txt"] = "SRC1" };
        var hints = new Dictionary<string, string> { ["b.txt"] = "SRC2" };

        Assert.Equal("SRC1", IconRouter.Route("a.txt", p => owner.GetValueOrDefault(p), hints, "P"));   // 持有优先
        Assert.Equal("SRC2", IconRouter.Route("b.txt", p => owner.GetValueOrDefault(p), hints, "P"));   // hint 次之
        Assert.Equal("P", IconRouter.Route("c.txt", p => owner.GetValueOrDefault(p), hints, "P"));      // 兜底主屏
    }

    [Fact]
    public void IconRouter_OrphanSkipped()
    {
        var orphans = new HashSet<string> { "gone.txt" };
        Assert.Null(IconRouter.Route("gone.txt", _ => null, EmptyHints, "P", orphans));
    }

    [Fact]
    public void IconRouter_SplitFor_OnlyOwnItems()
    {
        var owner = new Dictionary<string, string> { ["f.txt"] = "SRC1" };
        var hints = new Dictionary<string, string> { ["h.txt"] = "SRC2" };
        var all = new List<IconItem>
        {
            new("f.txt", "f"), new("h.txt", "h"), new("x.txt", "x"), new("y.txt", "y"),
        };
        // primary 传主屏 "P"（与被切屏 SRC1 不同）：无归属项落 P，不混入 SRC1
        var s1 = IconRouter.SplitFor("SRC1", all, p => owner.GetValueOrDefault(p), hints, "P");
        Assert.Equal(["f.txt"], s1.Select(i => i.FilePath));
        var s2 = IconRouter.SplitFor("SRC2", all, p => owner.GetValueOrDefault(p), hints, "SRC1");
        Assert.Equal(["h.txt"], s2.Select(i => i.FilePath));
        // 主屏兜底：无归属无 hint 的 x/y 落主屏（SplitFor 的 primary 参数即主屏）
        var sp = IconRouter.SplitFor("SRC1", all, p => owner.GetValueOrDefault(p), hints, "P");
        Assert.DoesNotContain("x.txt", sp.Select(i => i.FilePath));
        var spAll = IconRouter.SplitFor("P", all, p => owner.GetValueOrDefault(p), hints, "P");
        Assert.Equal(["x.txt", "y.txt"], spAll.Select(i => i.FilePath).OrderBy(x => x).ToList());
    }

    // ---------- WallpaperResolver ----------

    private static WallpaperConfig W(string mon, string path = "w.jpg") => new() { MonitorId = mon, Path = path };

    [Fact]
    public void WallpaperResolver_GroupBeatsStandalone()
    {
        var wallpapers = new List<WallpaperConfig> { W("SRC1", "standalone.jpg") };
        var groups = new List<DisplayGroup>
        {
            new() { Id = "g1", Name = "g", MonitorIds = new[] { "SRC1", "SRC2" }, WallpaperPath = "group.jpg" },
        };
        var (cfg, group) = WallpaperResolver.Resolve("SRC1", wallpapers, groups);
        Assert.Equal("group.jpg", cfg!.Path);
        Assert.Equal("g1", group!.Id);
    }

    [Fact]
    public void WallpaperResolver_StandaloneWhenNoGroup()
    {
        var wallpapers = new List<WallpaperConfig> { W("SRC1", "standalone.jpg") };
        var (cfg, group) = WallpaperResolver.Resolve("SRC1", wallpapers, new List<DisplayGroup>());
        Assert.Equal("standalone.jpg", cfg!.Path);
        Assert.Null(group);
    }

    [Fact]
    public void WallpaperResolver_GroupWithoutWallpaper_Ignored()
    {
        var wallpapers = new List<WallpaperConfig> { W("SRC1", "standalone.jpg") };
        var groups = new List<DisplayGroup>
        {
            new() { Id = "g1", MonitorIds = new[] { "SRC1" }, WallpaperPath = "" }, // 组无壁纸
        };
        var (cfg, _) = WallpaperResolver.Resolve("SRC1", wallpapers, groups);
        Assert.Equal("standalone.jpg", cfg!.Path);
    }

    [Fact]
    public void WallpaperResolver_CalcCanvas_TwoScreens()
    {
        var screens = new List<(string, IntRect)>
        {
            ("SRC1", new IntRect(0, 0, 1920, 1080)),
            ("SRC2", new IntRect(-1920, 0, 0, 1080)),
        };
        var r = WallpaperResolver.CalcCanvas("SRC2", screens);
        Assert.NotNull(r);
        Assert.Equal(3840, r!.Value.Canvas.Width);   // 画布=两屏 bounding box
        Assert.Equal(-1920, r.Value.MonRect.Left);    // 本屏 rect 原样
        Assert.Null(WallpaperResolver.CalcCanvas("SRC2", [screens[0]])); // 单屏降级
    }

    // ---------- TopologyRebuild ----------

    private static AppConfig Snapshot(params object[] _) => new();

    [Fact]
    public void TopologyRebuild_OfflineMonitorDataBecomesOrphan()
    {
        var cfg = new AppConfig
        {
            Fences = new List<FenceConfig> { new() { Id = "f1", MonitorId = "SRC1", IconFilePaths = new[] { "a.txt" } } },
            IconPositions = new List<IconPosition> { new("b.txt", 10, 10, "SRC1"), new("c.txt", 5, 5, "SRC2") },
        };
        // SRC1 离线、SRC2 在线
        var r = TopologyRebuild.Calculate(cfg, new[] { "SRC2" });

        Assert.Equal(["f1"], r.OrphanFences.Select(f => f.Id));          // SRC1 的 fence 成孤儿
        Assert.Equal(["b.txt"], r.OrphanPositions.Select(p => p.FilePath));
        Assert.True(r.OrphanPaths.Contains("a.txt"));                      // fence 内容 path 防误分发
        Assert.True(r.OrphanPaths.Contains("b.txt"));
        Assert.False(r.OrphanPaths.Contains("c.txt"));                     // 在线屏正常
        Assert.Equal(["c.txt"], r.PositionsByMon["SRC2"].Select(p => p.FilePath));
        Assert.Empty(r.FencesByMon["SRC2"]);
    }

    [Fact]
    public void TopologyRebuild_OnlineMonitorGetsItsData()
    {
        var cfg = new AppConfig
        {
            Fences = new List<FenceConfig>
            {
                new() { Id = "f1", MonitorId = "SRC1" },
                new() { Id = "f2", MonitorId = "SRC2" },
            },
            IconPositions = new List<IconPosition> { new("a.txt", 1, 1, "SRC1"), new("b.txt", 2, 2, "SRC2") },
        };
        var r = TopologyRebuild.Calculate(cfg, new[] { "SRC1", "SRC2" });
        Assert.Empty(r.OrphanFences);
        Assert.Empty(r.OrphanPaths);
        Assert.Equal(["f1"], r.FencesByMon["SRC1"].Select(f => f.Id));
        Assert.Equal(["b.txt"], r.PositionsByMon["SRC2"].Select(p => p.FilePath));
        Assert.Equal(2, r.LooseHints.Count); // 两条位置记录都在线 → 全部成为 hint
    }
}
