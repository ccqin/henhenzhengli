using DesktopManager.Core.Models;
using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

/// <summary>M5-T1：DisplayGroup config round-trip + 旧 JSON 兼容。</summary>
public class DisplayGroupTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    [Fact]
    public void DisplayGroup_RoundTrips()
    {
        var path = TempPath();
        try
        {
            var store = new ConfigStore(path);
            var config = new AppConfig
            {
                DisplayGroups = new[]
                {
                    new DisplayGroup
                    {
                        Id = "g1", Name = "双屏组",
                        MonitorIds = new[] { "MON#src0", "MON#src1" },
                        WallpaperKind = WallpaperKind.Video,
                        WallpaperPath = @"C:\v\a.mp4"
                    }
                }
            };
            store.Save(config);
            var loaded = store.Load();
            Assert.Single(loaded.DisplayGroups);
            var g = loaded.DisplayGroups[0];
            Assert.Equal("g1", g.Id);
            Assert.Equal(2, g.MonitorIds.Count);
            Assert.Equal(WallpaperKind.Video, g.WallpaperKind);
            Assert.Equal(@"C:\v\a.mp4", g.WallpaperPath);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void LegacyJson_WithoutDisplayGroups_LoadsEmpty()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, """{ "Fences": [], "IconPositions": [], "Wallpapers": [] }""");
            Assert.Empty(new ConfigStore(path).Load().DisplayGroups);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

/// <summary>M5-T1：跨屏裁剪几何真值表。</summary>
public class CrossScreenLayoutTests
{
    [Fact]
    public void Canvas_IsBoundingBox()
    {
        var c = CrossScreenLayout.Canvas(new[]
        {
            new IntRect(-1920, 0, 0, 1080),
            new IntRect(0, 0, 1920, 1080)
        });
        Assert.Equal(new IntRect(-1920, 0, 1920, 1080), c);
    }

    [Fact]
    public void Canvas_Empty_Null()
        => Assert.Null(CrossScreenLayout.Canvas(Array.Empty<IntRect>()));

    [Fact]
    public void Crop_ExactSizeBitmap_SplitsInHalf()
    {
        // 画布 3840x1080，源图 3840x1080 → scale=1，左屏裁左半
        var canvas = new IntRect(0, 0, 3840, 1080);
        var left = new IntRect(0, 0, 1920, 1080);
        var right = new IntRect(1920, 0, 3840, 1080);
        Assert.Equal((0, 0, 1920, 1080), CrossScreenLayout.CropRect(3840, 1080, canvas, left));
        Assert.Equal((1920, 0, 1920, 1080), CrossScreenLayout.CropRect(3840, 1080, canvas, right));
    }

    [Fact]
    public void Crop_WideBitmap_CoverCropsVerticallyCentered()
    {
        // 画布 3840x1080（比例 3.56），源图 7680x4320（16:9）→ cover scale = 3840/7680=0.5 vs 1080/4320=0.25 → 0.5
        // 缩放后 3840x2160，垂直多出 1080 居中裁 → offsetY=540 画布单位 → 源像素 offsetY/scale=1080
        var canvas = new IntRect(0, 0, 3840, 1080);
        var left = new IntRect(0, 0, 1920, 1080);
        var (x, y, w, h) = CrossScreenLayout.CropRect(7680, 4320, canvas, left);
        Assert.Equal(0, x);
        Assert.Equal(1080, y);
        Assert.Equal(3840, w);
        Assert.Equal(2160, h);
    }

    [Fact]
    public void Crop_SmallBitmap_Upscales()
    {
        // 源图 1920x540，画布 3840x1080 → scale = max(2, 2) = 2 → 源全图分两半
        var canvas = new IntRect(0, 0, 3840, 1080);
        var right = new IntRect(1920, 0, 3840, 1080);
        var (x, y, w, h) = CrossScreenLayout.CropRect(1920, 540, canvas, right);
        Assert.Equal(960, x);
        Assert.Equal(0, y);
        Assert.Equal(960, w);
        Assert.Equal(540, h);
    }
}
