using DesktopManager.Core.Models;

namespace DesktopManager.Core.Services;

/// <summary>桌面快照对账结果：Added = current 有 previous 无；Removed = previous 有 current 无。
/// M1 只做按 FilePath（OrdinalIgnoreCase）的 Added/Removed，不做 rename 推断（留 M2）。</summary>
public record DesktopDiff(
    IReadOnlyList<IconItem> Added,
    IReadOnlyList<IconItem> Removed)
{
    /// <summary>对比两次快照，返回 Added/Removed 变更集。</summary>
    public static DesktopDiff Diff(IReadOnlyList<IconItem> previous, IReadOnlyList<IconItem> current)
    {
        var prevByPath = previous.ToDictionary(i => i.FilePath, StringComparer.OrdinalIgnoreCase);
        var curByPath  = current.ToDictionary(i => i.FilePath, StringComparer.OrdinalIgnoreCase);

        var added   = current.Where(c => !prevByPath.ContainsKey(c.FilePath)).ToList();
        var removed = previous.Where(p => !curByPath.ContainsKey(p.FilePath)).ToList();
        return new DesktopDiff(added, removed);
    }
}
