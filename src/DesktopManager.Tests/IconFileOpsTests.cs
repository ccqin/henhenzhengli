using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

// M2-T5：重命名路径解析/校验纯逻辑单测（TDD）。
// 文件操作本身（File.Move / 回收站删除）依赖真实 FS，不在此单测（真机 T7 验）。
// fixture 全部用 Path.GetTempPath() 隔离，不碰开发机真桌面。
public class IconFileOpsTests
{
    private static string TempFile(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllText(path, "t5");
        return path;
    }

    [Fact]
    public void ResolveRenamePath_ValidNewName_ReturnsCombinedNewPath()
    {
        var oldPath = TempFile("t5-old-" + Path.GetRandomFileName() + ".txt");
        try
        {
            var result = IconFileOps.ResolveRenamePath(oldPath, "renamed.txt");
            Assert.True(result.Ok);
            Assert.Equal(Path.Combine(Path.GetTempPath(), "renamed.txt"), result.NewPath);
            Assert.Null(result.Error);
        }
        finally { if (File.Exists(oldPath)) File.Delete(oldPath); }
    }

    [Fact]
    public void ResolveRenamePath_EmptyNewName_ReturnsError()
    {
        var oldPath = TempFile("t5-empty-" + Path.GetRandomFileName() + ".txt");
        try
        {
            var result = IconFileOps.ResolveRenamePath(oldPath, "");
            Assert.False(result.Ok);
            Assert.Null(result.NewPath);
            Assert.NotEmpty(result.Error!);
        }
        finally { if (File.Exists(oldPath)) File.Delete(oldPath); }
    }

    [Fact]
    public void ResolveRenamePath_WhitespaceNewName_ReturnsError()
    {
        var oldPath = TempFile("t5-ws-" + Path.GetRandomFileName() + ".txt");
        try
        {
            var result = IconFileOps.ResolveRenamePath(oldPath, "   ");
            Assert.False(result.Ok);
            Assert.NotEmpty(result.Error!);
        }
        finally { if (File.Exists(oldPath)) File.Delete(oldPath); }
    }

    [Fact]
    public void ResolveRenamePath_SameAsOldName_ReturnsError_CaseInsensitive()
    {
        // Windows 文件系统大小写不敏感：仅改大小写的"重命名"会被同名检查拦下
        // （避免后续 File.Exists 误报冲突 + File.Move 语义模糊）。T5 不支持纯大小写重命名（YAGNI）。
        var oldPath = TempFile("t5-same-" + Path.GetRandomFileName() + ".txt");
        try
        {
            var oldName = Path.GetFileName(oldPath);
            var result = IconFileOps.ResolveRenamePath(oldPath, oldName);
            Assert.False(result.Ok);
            Assert.NotEmpty(result.Error!);

            // 大小写不同也算相同（OrdinalIgnoreCase）
            var upper = oldName.ToUpperInvariant();
            var result2 = IconFileOps.ResolveRenamePath(oldPath, upper);
            Assert.False(result2.Ok);
        }
        finally { if (File.Exists(oldPath)) File.Delete(oldPath); }
    }

    [Fact]
    public void ResolveRenamePath_InvalidChars_ReturnsError()
    {
        var oldPath = TempFile("t5-inv-" + Path.GetRandomFileName() + ".txt");
        try
        {
            // 含路径分隔符 / 非法字符（Path.GetInvalidFileNameChars）
            var result = IconFileOps.ResolveRenamePath(oldPath, "bad/name.txt");
            Assert.False(result.Ok);
            Assert.NotEmpty(result.Error!);

            var result2 = IconFileOps.ResolveRenamePath(oldPath, "bad<name.txt");
            Assert.False(result2.Ok);
        }
        finally { if (File.Exists(oldPath)) File.Delete(oldPath); }
    }

    [Fact]
    public void ResolveRenamePath_TargetExists_ReturnsError_NoOverwrite()
    {
        // 冲突目标真实存在（不同于原文件）→ 拒绝覆盖
        var oldPath = TempFile("t5-src-" + Path.GetRandomFileName() + ".txt");
        var targetPath = Path.Combine(Path.GetTempPath(), "t5-taken-" + Path.GetRandomFileName() + ".txt");
        File.WriteAllText(targetPath, "blocker");
        try
        {
            var result = IconFileOps.ResolveRenamePath(oldPath, Path.GetFileName(targetPath));
            Assert.False(result.Ok);
            Assert.NotEmpty(result.Error!);
        }
        finally
        {
            if (File.Exists(oldPath)) File.Delete(oldPath);
            if (File.Exists(targetPath)) File.Delete(targetPath);
        }
    }

    [Fact]
    public void ResolveRenamePath_TrimsWhitespaceAroundNewName()
    {
        var oldPath = TempFile("t5-trim-" + Path.GetRandomFileName() + ".txt");
        try
        {
            var result = IconFileOps.ResolveRenamePath(oldPath, "  trimmed.txt  ");
            Assert.True(result.Ok);
            Assert.Equal(Path.Combine(Path.GetTempPath(), "trimmed.txt"), result.NewPath);
        }
        finally { if (File.Exists(oldPath)) File.Delete(oldPath); }
    }
}
