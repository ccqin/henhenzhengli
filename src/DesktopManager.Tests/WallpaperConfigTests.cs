using DesktopManager.Core.Models;
using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

/// <summary>M4-T1：壁纸配置模型 + 持久化兼容。</summary>
public class WallpaperConfigTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    [Fact]
    public void Wallpaper_RoundTrips_WithKind()
    {
        var path = TempPath();
        try
        {
            var store = new ConfigStore(path);
            var config = new AppConfig
            {
                Wallpapers = new[]
                {
                    new WallpaperConfig { MonitorId = "MON#src0", Kind = WallpaperKind.Image, Path = @"C:\img\a.jpg" },
                    new WallpaperConfig { MonitorId = "MON#src1", Kind = WallpaperKind.Video, Path = @"C:\img\b.mp4" },
                    new WallpaperConfig { MonitorId = "MON#src2", Kind = WallpaperKind.Gif, Path = @"C:\img\c.gif" }
                }
            };

            store.Save(config);
            var loaded = store.Load();

            Assert.Equal(3, loaded.Wallpapers.Count);
            Assert.Equal(WallpaperKind.Image, loaded.Wallpapers[0].Kind);
            Assert.Equal(WallpaperKind.Video, loaded.Wallpapers[1].Kind);
            Assert.Equal(WallpaperKind.Gif, loaded.Wallpapers[2].Kind);
            Assert.Equal(@"C:\img\b.mp4", loaded.Wallpapers[1].Path);
            Assert.Equal("MON#src1", loaded.Wallpapers[1].MonitorId);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void LegacyJson_WithoutWallpapers_LoadsEmpty()
    {
        var path = TempPath();
        try
        {
            // M3 时代的 config：无 Wallpapers 字段
            File.WriteAllText(path, """
            {
              "Fences": [],
              "IconPositions": []
            }
            """);
            var loaded = new ConfigStore(path).Load();
            Assert.Empty(loaded.Wallpapers);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Defaults_AreSafe()
    {
        var w = new WallpaperConfig();
        Assert.Equal("", w.MonitorId);
        Assert.Equal("", w.Path);
        Assert.Equal(WallpaperKind.Image, w.Kind);
        Assert.Empty(new AppConfig().Wallpapers);
    }
}

/// <summary>M4-T5：扩展名 → Kind 判定。</summary>
public class WallpaperKindDetectionTests
{
    [Theory]
    [InlineData("a.jpg", WallpaperKind.Image)]
    [InlineData("a.PNG", WallpaperKind.Image)]
    [InlineData("a.gif", WallpaperKind.Gif)]
    [InlineData("a.mp4", WallpaperKind.Video)]
    [InlineData("a.WMV", WallpaperKind.Video)]
    [InlineData("a.avi", WallpaperKind.Video)]
    [InlineData("a.txt", WallpaperKind.Image)]
    public void DetectKind_Maps(string path, WallpaperKind expected)
        => Assert.Equal(expected, WallpaperConfig.DetectKind(path));
}
