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
        try
        {
            if (!File.Exists(_path))
                return new AppConfig();
            var json = File.ReadAllText(_path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options)
                      ?? new AppConfig();
            // Fences/IconPositions 防御性 null-coalesce：旧 config（无 IconPositions 字段）或 JSON 显式 null 时兜底空集合。
            return cfg with
            {
                Fences = cfg.Fences ?? Array.Empty<FenceConfig>(),
                IconPositions = cfg.IconPositions ?? Array.Empty<IconPosition>(),
                Wallpapers = cfg.Wallpapers ?? Array.Empty<WallpaperConfig>()
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // P1（原 TODO M1）：配置文件损坏/读取失败已兜底返回默认配置，但需记录以便诊断反复损坏的根因。
            Log.Warning(ex, "ConfigStore.Load 失败，返回空配置（路径={Path})", _path);
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
        if (File.Exists(_path)) File.Replace(tmp, _path, destinationBackupFileName: null);
        else File.Move(tmp, _path);
    }
}
