using DesktopManager.Core.Models;

namespace DesktopManager.Core.Services;

/// <summary>拓扑重建的孤儿/归属计算纯函数（M6 从 MultiMonitorHost 下沉，可单测）。
/// 以聚合快照为新基线 + 当前在线屏集合 → 孤儿（离线屏数据，保存时带回）+ 每屏 Fence/位置切分 + 分发 hint。</summary>
public static class TopologyRebuild
{
    public sealed record Result(
        List<FenceConfig> OrphanFences,
        List<IconPosition> OrphanPositions,
        HashSet<string> OrphanPaths,
        Dictionary<string, string> LooseHints,
        Dictionary<string, List<FenceConfig>> FencesByMon,
        Dictionary<string, List<IconPosition>> PositionsByMon);

    /// <summary>online：在线屏（持久 ID）集合。</summary>
    public static Result Calculate(AppConfig snapshot, IReadOnlyCollection<string> onlineIds)
    {
        var online = onlineIds.Select(id => new MonitorRef(id, false)).ToList();
        var fenceAssign = MonitorAssignment.FenceAssignments(snapshot.Fences, online);
        var looseAssign = MonitorAssignment.LooseAssignments(snapshot.IconPositions, online);

        var orphanFences = new List<FenceConfig>();
        var orphanPositions = new List<IconPosition>();
        var orphanPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in snapshot.Fences)
        {
            if (fenceAssign[f.Id] is not null) continue;
            orphanFences.Add(f);
            foreach (var p in f.IconFilePaths) orphanPaths.Add(p);
        }
        foreach (var p in snapshot.IconPositions)
        {
            if (looseAssign[p.FilePath] is not null) continue;
            orphanPositions.Add(p);
            orphanPaths.Add(p.FilePath);
        }

        var hints = looseAssign
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase);

        var fencesByMon = onlineIds.ToDictionary(id => id, _ => new List<FenceConfig>(), StringComparer.Ordinal);
        var positionsByMon = onlineIds.ToDictionary(id => id, _ => new List<IconPosition>(), StringComparer.Ordinal);
        foreach (var f in snapshot.Fences)
        {
            var a = fenceAssign[f.Id];
            if (a is not null && fencesByMon.TryGetValue(a, out var list)) list.Add(f);
        }
        foreach (var p in snapshot.IconPositions)
        {
            var a = looseAssign[p.FilePath];
            if (a is not null && positionsByMon.TryGetValue(a, out var list)) list.Add(p);
        }

        return new Result(orphanFences, orphanPositions, orphanPaths, hints, fencesByMon, positionsByMon);
    }
}
