using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using DesktopManager.Core.Models;
using Serilog;
namespace DesktopManager.Core.Services;

public sealed class ConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;

    public ConfigStore(string path) => _path = path;

    public AppConfig Load()
    {
        if (TryRead(_path, out var cfg)) return cfg;
        // 主文件损坏 → 回退备份（Save 的 File.Replace backup，始终保留上一版）。
        // 真机教训：配置被异常态清空/写坏后无备份可回，图标布局全丢。
        if (TryRead(_path + ".backup", out var backup))
        {
            Log.Warning("config 主文件不可用，已回退备份上一版（路径={Path}）", _path);
            return backup;
        }
        Log.Warning("config 主文件与备份均不可用（首次启动或全部损坏），返回空配置（路径={Path}）", _path);
        return new AppConfig();
    }

    /// <summary>读取并规范化（Fences 等集合字段 null-coalesce，兼容旧 config 无字段/显式 null）。
    /// 文件不存在或损坏返回 false（由调用方决定回退路径）。</summary>
    private bool TryRead(string path, out AppConfig cfg)
    {
        cfg = new AppConfig();
        try
        {
            if (!File.Exists(path)) return false;
            var read = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options);
            if (read is null) return false;
            cfg = read with
            {
                Fences = read.Fences ?? Array.Empty<FenceConfig>(),
                IconPositions = read.IconPositions ?? Array.Empty<IconPosition>(),
                Wallpapers = read.Wallpapers ?? Array.Empty<WallpaperConfig>(),
                DisplayGroups = read.DisplayGroups ?? Array.Empty<DisplayGroup>()
            };
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Warning(ex, "ConfigStore 读取失败（路径={Path}）", path);
            return false;
        }
    }

    public void Save(AppConfig config)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
        // backup：File.Replace 自动保留上一版（覆盖旧 backup）——主文件损坏时 Load 的回退源
        if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".backup");
        else File.Move(tmp, _path);
    }
}
