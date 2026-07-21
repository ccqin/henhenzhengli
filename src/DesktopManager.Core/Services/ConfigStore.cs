using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using DesktopManager.Core.Models;
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
        if (!File.Exists(_path))
            return new AppConfig(HideExplorerIcons: false, AutoStart: true, Fences: Array.Empty<FenceConfig>());
        var json = File.ReadAllText(_path);
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options)
                  ?? new AppConfig(Fences: Array.Empty<FenceConfig>());
        return cfg with { Fences = cfg.Fences ?? Array.Empty<FenceConfig>() };
    }

    public void Save(AppConfig config) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(config, Options));
}
