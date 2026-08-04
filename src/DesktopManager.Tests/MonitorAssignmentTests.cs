using DesktopManager.Core.Models;
using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

/// <summary>M3-T2：显示器归属求解（纯逻辑）。
/// 契约：config 里的 MonitorId → 在线显示器匹配；空串 = 主屏；不在线（拔线）= null（孤儿，不渲染）。</summary>
public class MonitorAssignmentTests
{
    private static readonly MonitorRef Primary = new("PCI#A#src0", IsPrimary: true);
    private static readonly MonitorRef Secondary = new("PCI#A#src1", IsPrimary: false);
    private static readonly MonitorRef[] Both = { Primary, Secondary };

    // ---------- Resolve 单条 ----------

    [Fact]
    public void Resolve_MatchingOnlineId_ReturnsIt()
        => Assert.Equal("PCI#A#src1", MonitorAssignment.Resolve("PCI#A#src1", Both));

    [Fact]
    public void Resolve_EmptyId_ReturnsPrimary()
        => Assert.Equal("PCI#A#src0", MonitorAssignment.Resolve("", Both));

    [Fact]
    public void Resolve_NullId_ReturnsPrimary()
        => Assert.Equal("PCI#A#src0", MonitorAssignment.Resolve(null, Both));

    [Fact]
    public void Resolve_EmptyId_NoPrimary_ReturnsNull()
        => Assert.Null(MonitorAssignment.Resolve("", new[] { Secondary }));

    [Fact]
    public void Resolve_OfflineId_ReturnsNull()
        => Assert.Null(MonitorAssignment.Resolve("PCI#A#src99", Both));

    [Fact]
    public void Resolve_NoMonitors_ReturnsNull()
    {
        Assert.Null(MonitorAssignment.Resolve("PCI#A#src0", Array.Empty<MonitorRef>()));
        Assert.Null(MonitorAssignment.Resolve("", Array.Empty<MonitorRef>()));
    }

    [Fact]
    public void Resolve_IsOrdinalCaseSensitive()
        => Assert.Null(MonitorAssignment.Resolve("pci#a#src0", Both));

    // ---------- 批量 ----------

    [Fact]
    public void FenceAssignments_MapsEachFence()
    {
        var fences = new[]
        {
            new FenceConfig { Id = "f1", MonitorId = "PCI#A#src1" },
            new FenceConfig { Id = "f2", MonitorId = "" },          // 缺省 → 主屏
            new FenceConfig { Id = "f3", MonitorId = "PCI#A#src99" } // 拔线 → 孤儿
        };
        var map = MonitorAssignment.FenceAssignments(fences, Both);
        Assert.Equal("PCI#A#src1", map["f1"]);
        Assert.Equal("PCI#A#src0", map["f2"]);
        Assert.Null(map["f3"]);
    }

    [Fact]
    public void LooseAssignments_MapsEachPath()
    {
        var positions = new[]
        {
            new IconPosition(@"C:\Users\u\Desktop\a.txt", 10, 20, "PCI#A#src1"),
            new IconPosition(@"C:\Users\u\Desktop\b.txt", 30, 40) // MonitorId 缺省 → 主屏
        };
        var map = MonitorAssignment.LooseAssignments(positions, Both);
        Assert.Equal("PCI#A#src1", map[@"C:\Users\u\Desktop\a.txt"]);
        Assert.Equal("PCI#A#src0", map[@"C:\Users\u\Desktop\b.txt"]);
    }

    [Fact]
    public void Assignments_NoMonitors_AllNull()
    {
        var fences = new[] { new FenceConfig { Id = "f1", MonitorId = "PCI#A#src0" } };
        var positions = new[] { new IconPosition("p", 0, 0, "PCI#A#src0") };
        Assert.Null(MonitorAssignment.FenceAssignments(fences, Array.Empty<MonitorRef>())["f1"]);
        Assert.Null(MonitorAssignment.LooseAssignments(positions, Array.Empty<MonitorRef>())["p"]);
    }
}
