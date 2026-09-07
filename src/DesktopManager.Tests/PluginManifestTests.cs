using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

/// <summary>M8-T1：插件清单解析（纯函数）。</summary>
public class PluginManifestTests
{
    [Fact]
    public void TryParse_Valid_ReturnsManifest()
    {
        var m = PluginManifest.TryParse("""
            {"id":"com.desktopmanager.pet","name":"桌面宠物","version":"1.0.0",
             "author":"official","entry":"DesktopManager.Plugin.Pet.exe",
             "zOrder":"above-wallpaper","clickThrough":false,"supportsPause":true}
            """, @"C:\plugins\pet");
        Assert.NotNull(m);
        Assert.Equal("com.desktopmanager.pet", m.Id);
        Assert.Equal("桌面宠物", m.Name);
        Assert.True(m.SupportsPause);
        Assert.False(m.ClickThrough);
        Assert.Equal(@"C:\plugins\pet\DesktopManager.Plugin.Pet.exe", m.EntryPath);
    }

    [Fact] // id 必须形如域前缀（含 '.'）
    public void TryParse_BadId_ReturnsNull()
    {
        Assert.Null(PluginManifest.TryParse("""{"id":"pet","name":"x","entry":"a.exe"}""", "d"));
        Assert.Null(PluginManifest.TryParse("""{"id":"","name":"x","entry":"a.exe"}""", "d"));
    }

    [Fact] // entry 含路径分隔符 = 越权执行目录外程序，拒绝
    public void TryParse_EntryWithSeparator_ReturnsNull()
    {
        Assert.Null(PluginManifest.TryParse("""{"id":"a.b","name":"x","entry":"..\\evil.exe"}""", "d"));
        Assert.Null(PluginManifest.TryParse("""{"id":"a.b","name":"x","entry":"sub/a.exe"}""", "d"));
    }

    [Fact] // name 必填；坏 JSON 返回 null
    public void TryParse_MissingNameOrBadJson_ReturnsNull()
    {
        Assert.Null(PluginManifest.TryParse("""{"id":"a.b","entry":"a.exe"}""", "d"));
        Assert.Null(PluginManifest.TryParse("{ not json", "d"));
    }
}
