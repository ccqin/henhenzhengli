using System.IO;

namespace DesktopManager.Plugin.Pet;

/// <summary>Live2D 渲染器骨架：WebView2 透明窗 + pixi.js + pixi-live2d-display。
/// 素材目录约定：<插件目录>\assets\&lt;角色&gt;\*.model3.json 存在 → Live2D；
/// 否则回退 Emoji 渲染器。模型文件由素材包/下载脚本放入。</summary>
internal sealed class Live2DAvailability
{
    /// <summary>查找第一个可用的 .model3.json（插件目录 assets 下递归一层）。</summary>
    public static string? FindModel(string pluginDir)
    {
        var assets = Path.Combine(pluginDir, "assets");
        if (!Directory.Exists(assets)) return null;
        foreach (var dir in Directory.EnumerateDirectories(assets))
        {
            var m = Directory.EnumerateFiles(dir, "*.model3.json").FirstOrDefault();
            if (m is not null) return m;
        }
        return Directory.EnumerateFiles(assets, "*.model3.json").FirstOrDefault();
    }
}
