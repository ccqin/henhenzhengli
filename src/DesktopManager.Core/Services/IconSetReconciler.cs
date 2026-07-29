using DesktopManager.Core.Models;

namespace DesktopManager.Core.Services;

/// <summary>P0-T3：散落图标集合对账纯逻辑（无 UI，无副作用，纯 IEnumerable 操作）。
/// <list type="bullet">
/// <item><see cref="PlanSnapshot"/>：启动/explorer 重启全量对账（算 toAdd/toRemove 差异集）。</item>
/// <item><see cref="PlanDiff"/>：sync.Changed 增量对账（从 <see cref="DesktopDiff"/> 推 toAdd/toRemove）。</item>
/// </list> 匹配键：FilePath（OrdinalIgnoreCase，与 <see cref="DesktopDiff"/> 一致）。</summary>
public static class IconSetReconciler
{
    /// <summary>全量对账（启动/explorer 重启用）。
    /// <para>toAdd = <paramref name="all"/> 中非 fenced 且 <paramref name="currentLoose"/> 没有的；</para>
    /// <para>toRemove = <paramref name="currentLoose"/> 中 <paramref name="all"/> 已没有、或已 fenced 的。</para>
    /// 返回的 toAdd/toRemove 均为输入集合中的原实例引用（不新建），调用方据此 mutate _looseIcons。</summary>
    public static (List<IconItem> toAdd, List<IconItem> toRemove) PlanSnapshot(
        IReadOnlyList<IconItem> all,
        IReadOnlySet<string> fencedPaths,
        IReadOnlyList<IconItem> currentLoose)
    {
        var looseByPath = new Dictionary<string, IconItem>(currentLoose.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var li in currentLoose) looseByPath[li.FilePath] = li;

        var allByPath = new Dictionary<string, IconItem>(all.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var a in all) allByPath[a.FilePath] = a;

        var toAdd = new List<IconItem>();
        foreach (var a in all)
        {
            if (fencedPaths.Contains(a.FilePath)) continue;     // 已归属 Fence，不进散落区
            if (looseByPath.ContainsKey(a.FilePath)) continue;  // 已在散落区，不重加（幂等）
            toAdd.Add(a);
        }

        var toRemove = new List<IconItem>();
        foreach (var li in currentLoose)
        {
            // all 已没有 → 文件已删；或已 fenced → 归属变化。两种都需从散落区移除。
            if (!allByPath.ContainsKey(li.FilePath) || fencedPaths.Contains(li.FilePath))
                toRemove.Add(li);
        }

        return (toAdd, toRemove);
    }

    /// <summary>增量对账（sync.Changed 用）。
    /// <para>toAdd = <paramref name="diff"/>.Added 中非 fenced 且非 loose 的；复用 diff.Added 原项引用
    ///（<c>DesktopSnapshot</c> 已设 DisplayName=Path.GetFileName、X/Y=0），ApplyDiff 据此同实例填 _allItems 与 _looseIcons。</para>
    /// <para>toRemove = <paramref name="diff"/>.Removed 中实际仍出现在 <paramref name="currentLoose"/> 的 FilePath（按 path 匹配）。
    /// 不在 currentLoose 的 Removed（如原本就 fenced）不返回，避免无意义删除。</para>
    /// <para>R9 rename：DesktopDiff 已拆 Removed(旧)+Added(新)，两端各自处理 → 旧名移除、新名加入。</para></summary>
    public static (List<IconItem> toAdd, List<string> toRemove) PlanDiff(
        DesktopDiff diff,
        IReadOnlySet<string> fencedPaths,
        IReadOnlyList<IconItem> currentLoose)
    {
        var loosePaths = new HashSet<string>(currentLoose.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var li in currentLoose) loosePaths.Add(li.FilePath);

        var toAdd = new List<IconItem>();
        foreach (var added in diff.Added)
        {
            if (fencedPaths.Contains(added.FilePath)) continue; // 属 Fence，散落区不管
            if (loosePaths.Contains(added.FilePath)) continue;  // 已在散落区，不重加（与 PlanSnapshot 对称防御）
            // 复用 diff.Added 原项（DesktopSnapshot 已设 DisplayName=文件名、X/Y=0），
            // 保证 ApplyDiff 填 _allItems 与 _looseIcons 为同一实例（消除双实例，T6 单实例零歧义）。
            toAdd.Add(added);
        }

        var toRemove = new List<string>();
        foreach (var rem in diff.Removed)
        {
            if (loosePaths.Contains(rem.FilePath)) toRemove.Add(rem.FilePath);
        }

        return (toAdd, toRemove);
    }
}
