using System.IO;

namespace DesktopManager.Core.Services;

/// <summary>
/// M2 真机修复（拖拽统一 FileDrop）：把"外部文件拖入桌面"的目标路径解析抽成纯函数，
/// 避开真实 FS 便于单测（与 <see cref="IconFileOps.ResolveRenamePath"/> 同理念：写操作留在 App 层）。
/// 规则：已在任一桌面目录（用户/公共，由调用方在 <paramref name="existingOnDesktop"/> 里一并传入）的文件跳过
/// （Target=null）；其余落到目标桌面目录，同名冲突递增 (2)/(3)…（不覆盖现有文件）。
/// </summary>
public static class DesktopDropPlanner
{
    /// <summary>为每个 dropped 文件解析目标路径。</summary>
    /// <param name="droppedFiles">DoDragDrop FileDrop 的文件完整路径数组（可能含 null/空项，过滤）。</param>
    /// <param name="desktopDir">目标桌面目录（通常用户桌面 <c>SpecialFolder.Desktop</c>；公共桌面只读不写入）。</param>
    /// <param name="existingOnDesktop">当前桌面所有文件完整路径（用户+公共，OrdinalIgnoreCase）：
    ///     既用于判断"已在桌面"跳过，也作为同名冲突检测基线（避免批内多文件同名互相覆盖）。</param>
    /// <returns>每文件一个 <see cref="DesktopDropPlan"/>：Source 原路径；Target=目标路径（已在桌面则 null=跳过）。
    ///     顺序保持输入顺序（explorer 拖多文件时顺序可预期）。</returns>
    public static IReadOnlyList<DesktopDropPlan> Plan(
        IReadOnlyList<string>? droppedFiles,
        string desktopDir,
        HashSet<string>? existingOnDesktop)
    {
        if (droppedFiles is null || droppedFiles.Count == 0) return Array.Empty<DesktopDropPlan>();
        if (string.IsNullOrEmpty(desktopDir)) return Array.Empty<DesktopDropPlan>();

        var plans = new List<DesktopDropPlan>(droppedFiles.Count);
        // usedTargets：基线 = 当前桌面已有 + 本次已规划目标，保证批内同名也不互相覆盖。
        var usedTargets = new HashSet<string>(
            existingOnDesktop ?? (IEnumerable<string>)Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var src in droppedFiles)
        {
            if (string.IsNullOrWhiteSpace(src)) continue;
            // 已在桌面（含公共桌面，由调用方在 existingOnDesktop 一并传入）→ 跳过（Target=null）
            if (usedTargets.Contains(src))
            {
                plans.Add(new DesktopDropPlan(src, null));
                continue;
            }

            var name = Path.GetFileName(src);
            // 无文件名（如目录拖入或根路径）→ 跳过（M2 范围仅文件；目录拖入 YAGNI）
            if (string.IsNullOrEmpty(name)) continue;

            var target = Path.Combine(desktopDir, name);
            // 同名冲突递增 "name (2).ext"、"name (3).ext"…（与 explorer 冲突命名一致，不覆盖）
            int n = 2;
            while (usedTargets.Contains(target))
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                target = Path.Combine(desktopDir, $"{stem} ({n}){ext}");
                n++;
            }
            usedTargets.Add(target);
            plans.Add(new DesktopDropPlan(src, target));
        }
        return plans;
    }
}

/// <summary>单文件拖入桌面计划。<see cref="Target"/> 为 null 表示跳过（已在桌面）；
/// 否则调用方执行 <c>File.Move(Source, Target)</c>（跨卷降级 Copy+Delete）。</summary>
public record DesktopDropPlan(string Source, string? Target);
