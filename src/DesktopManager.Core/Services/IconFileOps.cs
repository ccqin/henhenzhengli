using System.IO;

namespace DesktopManager.Core.Services;

/// <summary>M2-T5：散落图标右键操作的纯逻辑（重命名路径解析/校验）。
/// 文件写操作（File.Move / 回收站删除）留在 App 层（依赖 Windows 专属 API + 真实 FS），
/// 此处只提供与 UI/FS 写无关的可测校验。</summary>
public static class IconFileOps
{
    /// <summary>解析并校验重命名目标路径。不执行任何写操作。
    /// 校验顺序：空名 → 与原名相同 → 非法字符 → 目标已存在。
    /// "相同"用 OrdinalIgnoreCase：Windows 文件系统大小写不敏感，
    /// 仅改大小写的重命名若放行会让后续 File.Exists 把原文件误报成冲突；T5 不支持纯大小写重命名（YAGNI）。</summary>
    public static RenameResult ResolveRenamePath(string oldPath, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return RenameResult.Fail("文件名不能为空。");

        var name = newName.Trim();

        var oldName = Path.GetFileName(oldPath);
        if (string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase))
            return RenameResult.Fail("新文件名与原文件名相同。");

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return RenameResult.Fail("文件名包含非法字符。");

        var dir = Path.GetDirectoryName(oldPath);
        var newPath = Path.Combine(dir!, name);
        if (File.Exists(newPath))
            return RenameResult.Fail("目标文件名已存在，未覆盖。");

        return RenameResult.Success(newPath);
    }
}

/// <summary>重命名校验结果。Ok=true 时 NewPath 有效；Ok=false 时 Error 为失败原因（供 UI 提示）。</summary>
public record RenameResult(bool Ok, string? NewPath, string? Error)
{
    public static RenameResult Success(string newPath) => new(true, newPath, null);
    public static RenameResult Fail(string error) => new(false, null, error);
}
