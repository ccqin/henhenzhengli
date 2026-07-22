using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

/// <summary>
/// T7 容错逻辑单测：加载 FenceConfig.IconFilePaths 时，跳过已不存在的路径
/// （用户可能已删除文件），避免渲染/归属崩溃。逻辑抽到 Core 以便不依赖 WPF 单测。
/// </summary>
public class IconPathFilterTests
{
    [Fact]
    public void FilterExisting_Returns_Only_Existing_Paths()
    {
        var existing1 = Path.GetTempFileName();
        var existing2 = Path.GetTempFileName();
        try
        {
            var paths = new[] { existing1, @"C:\does\not\exist\abc.txt", existing2 };
            var result = IconPathFilter.FilterExisting(paths);
            Assert.Equal(2, result.Count);
            Assert.Contains(existing1, result);
            Assert.Contains(existing2, result);
        }
        finally
        {
            if (File.Exists(existing1)) File.Delete(existing1);
            if (File.Exists(existing2)) File.Delete(existing2);
        }
    }

    [Fact]
    public void FilterExisting_Preserves_Order()
    {
        var a = Path.GetTempFileName();
        var b = Path.GetTempFileName();
        var c = Path.GetTempFileName();
        try
        {
            var result = IconPathFilter.FilterExisting(new[] { a, b, c });
            Assert.Equal(new[] { a, b, c }, result);
        }
        finally
        {
            foreach (var p in new[] { a, b, c }) if (File.Exists(p)) File.Delete(p);
        }
    }

    [Fact]
    public void FilterExisting_Skips_Whitespace_And_Empty()
    {
        var existing = Path.GetTempFileName();
        try
        {
            var result = IconPathFilter.FilterExisting(new[] { "  ", "", existing, null! });
            Assert.Single(result);
            Assert.Equal(existing, result[0]);
        }
        finally { if (File.Exists(existing)) File.Delete(existing); }
    }

    [Fact]
    public void FilterExisting_Empty_Input_Returns_Empty()
    {
        Assert.Empty(IconPathFilter.FilterExisting(Array.Empty<string>()));
    }

    [Fact]
    public void FilterExisting_All_Missing_Returns_Empty()
    {
        var result = IconPathFilter.FilterExisting(new[] { @"C:\nope\1.txt", @"C:\nope\2.txt" });
        Assert.Empty(result);
    }

    [Fact]
    public void FilterExisting_Dedupes_CaseInsensitive()
    {
        var existing = Path.GetTempFileName();
        try
        {
            // 同一文件不同大小写路径不应重复归属（与 _fencedPaths 的 OrdinalIgnoreCase 一致）。
            var result = IconPathFilter.FilterExisting(new[] { existing, existing.ToUpperInvariant() });
            Assert.Single(result);
        }
        finally { if (File.Exists(existing)) File.Delete(existing); }
    }
}
