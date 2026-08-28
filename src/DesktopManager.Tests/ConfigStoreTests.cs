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

    [Fact]
    public void Save_Load_RoundTrips_IconPositions()
    {
        // 自由摆放：散落图标持久化位置 round-trip（含小数坐标 + 多条目 + 中文路径）。
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var store = new ConfigStore(path);
            var config = new AppConfig
            {
                IconPositions = new[]
                {
                    new IconPosition(@"C:\Users\a\桌面\记事本.lnk", 123.45, 678.9),
                    new IconPosition(@"D:\work\report.docx", 0, 0),
                    new IconPosition(@"C:\b.txt", 16, 16)
                }
            };

            store.Save(config);
            var loaded = store.Load();

            Assert.Equal(3, loaded.IconPositions.Count);
            Assert.Equal(@"C:\Users\a\桌面\记事本.lnk", loaded.IconPositions[0].FilePath);
            Assert.Equal(123.45, loaded.IconPositions[0].X);
            Assert.Equal(678.9, loaded.IconPositions[0].Y);
            Assert.Equal(@"D:\work\report.docx", loaded.IconPositions[1].FilePath);
            Assert.Equal(0, loaded.IconPositions[1].X);
            Assert.Equal(@"C:\b.txt", loaded.IconPositions[2].FilePath);
            Assert.Equal(16, loaded.IconPositions[2].Y);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void AppConfig_Default_IconPositions_Empty()
    {
        // 默认值：新 AppConfig 无 IconPositions → 空集合（非 null），ConfigStore.Load 缺字段也走此默认。
        var cfg = new AppConfig();
        Assert.NotNull(cfg.IconPositions);
        Assert.Empty(cfg.IconPositions);
    }

    [Fact]
    public void Load_OldConfigWithoutIconPositions_ReturnsEmpty()
    {
        // 兼容旧 config（自由摆放前的版本，无 IconPositions 字段）→ Load 不抛、返回空集合。
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, @"{ ""HideExplorerIcons"": true, ""Fences"": [] }");
            var store = new ConfigStore(path);
            var loaded = store.Load();
            Assert.NotNull(loaded.IconPositions);
            Assert.Empty(loaded.IconPositions);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact] // 备份轮换：Save 两次后 .backup 保留上一版内容
    public void Save_Twice_BackupHoldsPreviousVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var store = new ConfigStore(path);
            store.Save(new AppConfig { Fences = new[] { new FenceConfig { Id = "v1", Title = "第一版" } } });
            store.Save(new AppConfig { Fences = new[] { new FenceConfig { Id = "v2", Title = "第二版" } } });

            Assert.True(File.Exists(path + ".backup"));
            var backup = store.Load(); // 主文件应是第二版
            Assert.Equal("v2", backup.Fences[0].Id);
            // backup 文件本身应是第一版
            var backupCfg = new ConfigStore(path + ".backup").Load();
            Assert.Equal("v1", backupCfg.Fences[0].Id);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".backup")) File.Delete(path + ".backup");
        }
    }

    [Fact] // 主文件损坏 → Load 回退备份（数据安全双保险）
    public void Load_CorruptedMainFile_FallsBackToBackup()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var store = new ConfigStore(path);
            store.Save(new AppConfig { Fences = new[] { new FenceConfig { Id = "好数据", Title = "T" } } });
            store.Save(new AppConfig { Fences = new[] { new FenceConfig { Id = "更新", Title = "T2" } } });
            File.WriteAllText(path, "{ 这是坏掉的 JSON"); // 模拟主文件损坏

            var loaded = store.Load();
            Assert.Single(loaded.Fences);
            Assert.Equal("好数据", loaded.Fences[0].Id); // 回退到备份的上一版
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".backup")) File.Delete(path + ".backup");
        }
    }
}
