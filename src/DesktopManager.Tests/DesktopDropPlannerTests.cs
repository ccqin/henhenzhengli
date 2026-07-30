using System.IO;
using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

// M2 真机修复（拖拽 FileDrop）：外部文件拖入桌面的目标路径解析纯函数单测。
// fixture 全部用 Path.GetTempPath() 隔离，不碰开发机真桌面；Plan 不碰 FS，只做字符串决策。
// 真实 File.Move / 跨卷降级 / FSW 检测由真机验收（见 fix-drag-filedrop-report.md 真机待验）。
public class DesktopDropPlannerTests
{
    private static string Tmp(string n) => Path.Combine(Path.GetTempPath(), n);

    [Fact]
    public void Plan_NullFiles_ReturnsEmpty()
    {
        var plans = DesktopDropPlanner.Plan(null!, Tmp("desk"), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(plans);
    }

    [Fact]
    public void Plan_EmptyFiles_ReturnsEmpty()
    {
        var plans = DesktopDropPlanner.Plan(Array.Empty<string>(), Tmp("desk"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(plans);
    }

    [Fact]
    public void Plan_EmptyDesktopDir_ReturnsEmpty_GuardsCaller()
    {
        // 调用方传空桌面目录（极端：GetFolderPath 返回空）不应崩，静默放弃。
        var plans = DesktopDropPlanner.Plan(new[] { Tmp("a.txt") }, "",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(plans);
    }

    [Fact]
    public void Plan_FileAlreadyOnDesktop_TargetsNull_Skip()
    {
        // 文件已在用户桌面（existingOnDesktop 含完整路径）→ Target=null，调用方不移动。
        var desktop = Tmp("desk");
        var file = Path.Combine(desktop, "have.txt");
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file };

        var plans = DesktopDropPlanner.Plan(new[] { file }, desktop, existing);

        Assert.Single(plans);
        Assert.Null(plans[0].Target);
        Assert.Equal(file, plans[0].Source);
    }

    [Fact]
    public void Plan_ExternalFile_TargetsDesktopCombinations()
    {
        // 外部文件（不在桌面）→ 目标 = desktopDir/fileName。
        var desktop = Tmp("desk");
        var src = Tmp("external-" + Path.GetRandomFileName() + ".txt");

        var plans = DesktopDropPlanner.Plan(new[] { src }, desktop,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Single(plans);
        Assert.NotNull(plans[0].Target);
        Assert.Equal(Path.Combine(desktop, Path.GetFileName(src)), plans[0].Target);
    }

    [Fact]
    public void Plan_NameClashWithExisting_IncrementsWith2()
    {
        // 桌面已有同名文件（不同路径）→ 新目标递增 "stem (2).ext"，不覆盖。
        var desktop = Tmp("desk");
        var src = Path.Combine(Tmp("folder"), "dup.txt");
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(desktop, "dup.txt") // 桌面已有同名
        };

        var plans = DesktopDropPlanner.Plan(new[] { src }, desktop, existing);

        Assert.Equal(Path.Combine(desktop, "dup (2).txt"), plans[0].Target);
    }

    [Fact]
    public void Plan_NameClashMultiple_IncrementsTo3()
    {
        // 桌面已有 "dup.txt" 和 "dup (2).txt" → 新目标 "dup (3).txt"。
        var desktop = Tmp("desk");
        var src = Path.Combine(Tmp("folder"), "dup.txt");
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(desktop, "dup.txt"),
            Path.Combine(desktop, "dup (2).txt")
        };

        var plans = DesktopDropPlanner.Plan(new[] { src }, desktop, existing);

        Assert.Equal(Path.Combine(desktop, "dup (3).txt"), plans[0].Target);
    }

    [Fact]
    public void Plan_BatchSameName_FilesGetDistinctTargets_NoOverwrite()
    {
        // 批内两个同名外部文件（不同源目录）→ 第一个落 dup.txt，第二个落 dup (2).txt（不互相覆盖）。
        var desktop = Tmp("desk");
        var src1 = Path.Combine(Tmp("dir1"), "dup.txt");
        var src2 = Path.Combine(Tmp("dir2"), "dup.txt");

        var plans = DesktopDropPlanner.Plan(new[] { src1, src2 }, desktop,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(2, plans.Count);
        Assert.Equal(Path.Combine(desktop, "dup.txt"), plans[0].Target);
        Assert.Equal(Path.Combine(desktop, "dup (2).txt"), plans[1].Target);
    }

    [Fact]
    public void Plan_NoExtension_PreservesNameInConflict()
    {
        // 无扩展名文件冲突 → "name (2)"（ext 为空字符串，拼接仍正确）。
        var desktop = Tmp("desk");
        var src = Path.Combine(Tmp("folder"), "README");
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(desktop, "README")
        };

        var plans = DesktopDropPlanner.Plan(new[] { src }, desktop, existing);

        Assert.Equal(Path.Combine(desktop, "README (2)"), plans[0].Target);
    }

    [Fact]
    public void Plan_NullOrWhitespaceItems_Filtered()
    {
        // FileDrop 数组含 null/空/空白项（畸形源）→ 过滤，不崩，不产生 plan。
        var desktop = Tmp("desk");
        var good = Tmp("good-" + Path.GetRandomFileName() + ".txt");

        var plans = DesktopDropPlanner.Plan(new string?[] { null, "", "   ", good }!, desktop,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Single(plans);
        Assert.Equal(good, plans[0].Source);
    }

    [Fact]
    public void Plan_PreservesInputOrder()
    {
        // 多文件顺序保持（explorer 拖多文件时目标对应可预期）。
        var desktop = Tmp("desk");
        var a = Path.Combine(Tmp("dir"), "a.txt");
        var b = Path.Combine(Tmp("dir"), "b.txt");
        var c = Path.Combine(Tmp("dir"), "c.txt");

        var plans = DesktopDropPlanner.Plan(new[] { a, b, c }, desktop,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(3, plans.Count);
        Assert.Equal(a, plans[0].Source);
        Assert.Equal(b, plans[1].Source);
        Assert.Equal(c, plans[2].Source);
    }

    [Fact]
    public void Plan_ExistingNull_TreatedAsEmpty()
    {
        // existingOnDesktop 传 null（调用方未初始化）→ 当空集处理，不崩。
        var desktop = Tmp("desk");
        var src = Tmp("x-" + Path.GetRandomFileName() + ".txt");

        var plans = DesktopDropPlanner.Plan(new[] { src }, desktop, null);

        Assert.Single(plans);
        Assert.Equal(Path.Combine(desktop, Path.GetFileName(src)), plans[0].Target);
    }

    [Fact]
    public void Plan_ExistingCaseInsensitive_AlreadyOnDesktopSkipped()
    {
        // 大小写不同但同名（C:\Desk\A.txt vs c:\desk\a.txt）→ 视为已在桌面跳过（Windows FS 大小写不敏感）。
        var desktop = "C:\\Desk";
        var src = "c:\\desk\\A.txt";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "C:\\Desk\\a.txt" };

        var plans = DesktopDropPlanner.Plan(new[] { src }, desktop, existing);

        Assert.Null(plans[0].Target);
    }
}
