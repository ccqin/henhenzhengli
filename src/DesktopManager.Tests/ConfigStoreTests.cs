using DesktopManager.Core.Models;
using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void Save_Load_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var store = new ConfigStore(path);
            var config = new AppConfig
            {
                HideExplorerIcons = true,
                AutoStart = true,
                Fences = new[] { new FenceConfig { Id = "f1", Title = "Work", X = 10, Y = 20, W = 300, H = 400 } }
            };

            store.Save(config);
            var loaded = store.Load();

            Assert.True(loaded.HideExplorerIcons);
            Assert.Single(loaded.Fences);
            Assert.Equal("Work", loaded.Fences[0].Title);
            Assert.Equal(300, loaded.Fences[0].W);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        var store = new ConfigStore(path);
        var loaded = store.Load();
        Assert.False(loaded.HideExplorerIcons); // 默认不接管，安全
        Assert.Empty(loaded.Fences);
    }

    [Fact]
    public void Save_Load_PreservesNonAsciiCharacters()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var store = new ConfigStore(path);
            var config = new AppConfig
            {
                HideExplorerIcons = true,
                AutoStart = false,
                Fences = new[] { new FenceConfig { Id = "f1", Title = "工作收纳盒", X = 0, Y = 0, W = 100, H = 100 } }
            };

            store.Save(config);
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("\\u", json); // 中文不应被转义成 \uXXXX
            var loaded = store.Load();
            Assert.Equal("工作收纳盒", loaded.Fences[0].Title);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_CorruptedJson_ReturnsDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{ this is not valid json");
            var store = new ConfigStore(path);
            var loaded = store.Load();   // 不应抛
            Assert.False(loaded.HideExplorerIcons);
            Assert.Empty(loaded.Fences);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_Load_RoundTrips_Fence_FoldedAndIconFilePaths()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var store = new ConfigStore(path);
            var config = new AppConfig
            {
                Fences = new[]
                {
                    new FenceConfig
                    {
                        Id = "f2",
                        Title = "游戏",
                        X = 12.5,
                        Y = 34.75,
                        W = 240,
                        H = 180,
                        Folded = true,
                        IconFilePaths = new[] { @"C:\a.txt", @"C:\b.txt", @"C:\c.txt" }
                    }
                }
            };

            store.Save(config);
            var loaded = store.Load();

            Assert.Single(loaded.Fences);
            var f = loaded.Fences[0];
            Assert.Equal("f2", f.Id);
            Assert.Equal("游戏", f.Title);
            Assert.Equal(12.5, f.X);
            Assert.Equal(34.75, f.Y);
            Assert.Equal(240, f.W);
            Assert.Equal(180, f.H);
            Assert.True(f.Folded);
            Assert.Equal(new[] { @"C:\a.txt", @"C:\b.txt", @"C:\c.txt" }, f.IconFilePaths);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void FenceConfig_Defaults_AreCorrect()
    {
        var f = new FenceConfig();

        Assert.Equal("", f.Id);
        Assert.Equal("", f.Title);
        Assert.Equal(0, f.X);
        Assert.Equal(0, f.Y);
        Assert.Equal(180, f.W);
        Assert.Equal(120, f.H);
        Assert.False(f.Folded);
        Assert.NotNull(f.IconFilePaths);
        Assert.Empty(f.IconFilePaths);
    }
}
