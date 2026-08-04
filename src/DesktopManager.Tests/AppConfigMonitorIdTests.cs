using DesktopManager.Core.Models;
using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

/// <summary>M3-T2：MonitorId 进 config 模型 + 持久化兼容。
/// 关键约束：旧 config（无 MonitorId 字段）Load 后 MonitorId 为空串（= 主屏），无迁移代码。</summary>
public class AppConfigMonitorIdTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    [Fact]
    public void MonitorId_RoundTrips()
    {
        var path = TempPath();
        try
        {
            var store = new ConfigStore(path);
            var config = new AppConfig
            {
                Fences = new[]
                {
                    new FenceConfig { Id = "f1", Title = "副屏盒", X = 1, Y = 2, MonitorId = "PCI#A#src1" }
                },
                IconPositions = new[]
                {
                    new IconPosition(@"C:\d\a.txt", 10, 20, "PCI#A#src1")
                }
            };

            store.Save(config);
            var loaded = store.Load();

            Assert.Equal("PCI#A#src1", loaded.Fences[0].MonitorId);
            Assert.Equal("PCI#A#src1", loaded.IconPositions[0].MonitorId);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void LegacyJson_WithoutMonitorId_LoadsEmpty()
    {
        var path = TempPath();
        try
        {
            // M2 时代的 config：无 MonitorId 字段
            File.WriteAllText(path, """
            {
              "Fences": [ { "Id": "f1", "Title": "旧盒", "X": 5, "Y": 6, "W": 180, "H": 120, "IconFilePaths": [] } ],
              "IconPositions": [ { "FilePath": "C:\\d\\a.txt", "X": 1, "Y": 2 } ]
            }
            """);
            var loaded = new ConfigStore(path).Load();
            Assert.Equal("", loaded.Fences[0].MonitorId);
            Assert.Equal("", loaded.IconPositions[0].MonitorId);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void IconPosition_DefaultMonitorId_IsEmpty()
        => Assert.Equal("", new IconPosition("p", 0, 0).MonitorId);

    [Fact]
    public void FenceConfig_DefaultMonitorId_IsEmpty()
        => Assert.Equal("", new FenceConfig().MonitorId);
}
