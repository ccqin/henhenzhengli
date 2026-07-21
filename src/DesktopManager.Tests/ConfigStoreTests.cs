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
                Fences = new[] { new FenceConfig("f1", "Work", 10, 20, 300, 400) }
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
                Fences = new[] { new FenceConfig("f1", "工作收纳盒", 0, 0, 100, 100) }
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
}
