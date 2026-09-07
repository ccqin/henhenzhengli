using System.IO;
using System.Text.Json;

namespace DesktopManager.Core.Services;

/// <summary>插件清单（plugin.json）——发现/加载/启停的最小契约。
/// 解析为纯函数（TryParse）保持单测隔离；文件系统访问由调用方做。</summary>
public sealed record PluginManifest
{
    public string Id { get; init; } = "";            // 如 com.desktopmanager.pet
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";
    public string Entry { get; init; } = "";          // 相对插件目录的 exe 名
    public string ZOrder { get; init; } = "above-wallpaper";  // above-wallpaper | above-icons（预留）
    public bool ClickThrough { get; init; }           // true = 全窗口点击穿透（氛围特效类）
    public bool SupportsPause { get; init; }          // 全屏/锁屏时接收 Pause/Resume
    public string Directory { get; init; } = "";      // 插件所在目录（解析后回填）

    public string EntryPath => Path.Combine(Directory, Entry);

    /// <summary>解析 plugin.json 文本。id/entry 必填且 id 形如域前缀（含 '.'）；
    /// entry 不含路径分隔符（防越权执行目录外程序）。</summary>
    public static PluginManifest? TryParse(string json, string directory)
    {
        try
        {
            var m = JsonSerializer.Deserialize<PluginManifest>(json, JsonOpts);
            if (m is null) return null;
            if (string.IsNullOrWhiteSpace(m.Id) || !m.Id.Contains('.')) return null;
            if (string.IsNullOrWhiteSpace(m.Entry) || m.Entry.Contains('/') || m.Entry.Contains('\\')) return null;
            if (string.IsNullOrWhiteSpace(m.Name)) return null;
            return m with { Directory = directory };
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>config.json 的 Plugins 节：启用列表 + 各插件配置（宿主代存）。</summary>
public sealed record PluginConfigState
{
    public List<string> Enabled { get; init; } = [];
    public Dictionary<string, Dictionary<string, string>> Configs { get; init; } = new();
}
