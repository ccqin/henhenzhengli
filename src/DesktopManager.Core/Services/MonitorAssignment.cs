using DesktopManager.Core.Models;

namespace DesktopManager.Core.Services;

/// <summary>M3-T2：布局归属求解（纯函数，可单测）。
/// 把 config 里的 MonitorId 解析到当前在线显示器：
/// <list type="bullet">
/// <item>在线且匹配 → 该屏 PersistentId（布局渲染到该屏窗口）</item>
/// <item>空串（旧 config 迁移 / 无归属记录）→ 当前主屏</item>
/// <item>不在线（拔线）→ null = 孤儿：不渲染，config 保留，插回后原位恢复</item>
/// </list>
/// 比较用 Ordinal（PersistentId 来自同一 API，大小写一致；拒绝大小写漂移导致的错配）。</summary>
public static class MonitorAssignment
{
    /// <summary>单条解析。configMonitorId 为 null/空 → 主屏；不在线或无显示器 → null。</summary>
    public static string? Resolve(string? configMonitorId, IReadOnlyList<MonitorRef> online)
    {
        if (online.Count == 0) return null;
        if (string.IsNullOrEmpty(configMonitorId))
            return online.FirstOrDefault(m => m.IsPrimary)?.PersistentId; // 无主屏（畸形拓扑）→ 孤儿
        return online.Any(m => string.Equals(m.PersistentId, configMonitorId, StringComparison.Ordinal))
            ? configMonitorId
            : null;
    }

    /// <summary>批量：FenceId → 归属屏（null=孤儿）。</summary>
    public static IReadOnlyDictionary<string, string?> FenceAssignments(
        IReadOnlyList<FenceConfig> fences, IReadOnlyList<MonitorRef> online)
    {
        var map = new Dictionary<string, string?>(fences.Count);
        foreach (var f in fences)
            map[f.Id] = Resolve(f.MonitorId, online);
        return map;
    }

    /// <summary>批量：散落图标 FilePath → 归属屏（null=孤儿）。key 用 Ordinal（path 大小写由文件系统归一）。</summary>
    public static IReadOnlyDictionary<string, string?> LooseAssignments(
        IReadOnlyList<IconPosition> positions, IReadOnlyList<MonitorRef> online)
    {
        var map = new Dictionary<string, string?>(positions.Count);
        foreach (var p in positions)
            map[p.FilePath] = Resolve(p.MonitorId, online);
        return map;
    }
}
