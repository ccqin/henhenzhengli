namespace DesktopManager.Core.Models;

public record AppConfig
{
    public bool HideExplorerIcons { get; init; } = false;
    public bool AutoStart { get; init; } = true;
    public IReadOnlyList<FenceConfig> Fences { get; init; } = Array.Empty<FenceConfig>();
    // 散落图标自由摆放位置（自由摆放特性）：重启后保持用户拖到的位置。
    // 仅记录散落（非 Fence 归属）图标；Fence 内图标位置由 FenceConfig.IconFilePaths 隐含。
    public IReadOnlyList<IconPosition> IconPositions { get; init; } = Array.Empty<IconPosition>();
    // M4：每屏壁纸配置（MonitorId 归属，孤儿语义同 M3：离线屏配置保留，插回恢复）。
    public IReadOnlyList<WallpaperConfig> Wallpapers { get; init; } = Array.Empty<WallpaperConfig>();
    // M5：显示组（组内屏共享壁纸源；成员屏渲染组优先于独立壁纸）。
    public IReadOnlyList<DisplayGroup> DisplayGroups { get; init; } = Array.Empty<DisplayGroup>();
    // 外观（M6 美化）：图标尺寸档（32/48/64）+ 文字标签风格（shadow=原生阴影 / pill=现代胶囊）。
    public AppearanceConfig Appearance { get; init; } = new();
}

public record AppearanceConfig
{
    public int IconSize { get; init; } = 48;
    public string LabelStyle { get; init; } = "shadow";
}

/// <summary>散落图标自由摆放位置持久化记录。FilePath 为键（OrdIgnoreCase），X/Y 为 LooseItemsControl 坐标系（与 IconCanvas 1:1）。
/// M3：MonitorId = 归属屏持久 ID（窗口本地坐标系）；空串=主屏（旧 config 兼容）。</summary>
public record IconPosition(string FilePath, double X, double Y, string MonitorId = "");

/// <summary>M4：壁纸源类型。以文件实际内容为准（SetWallpaper 按扩展名校正），config 存的只做提示。</summary>
public enum WallpaperKind
{
    Image,
    Video,
    Gif
}

/// <summary>M4：单屏壁纸配置。MonitorId=归属屏持久 ID；Path 空串 = 该屏无壁纸（系统壁纸透出）。</summary>
public record WallpaperConfig
{
    public string MonitorId { get; init; } = "";
    public WallpaperKind Kind { get; init; } = WallpaperKind.Image;
    public string Path { get; init; } = "";

    /// <summary>按扩展名判定 Kind（config.Kind 只做提示，加载时以文件实际为准，防用户改扩展名）。</summary>
    public static WallpaperKind DetectKind(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".gif" => WallpaperKind.Gif,
            ".mp4" or ".wmv" or ".avi" or ".m4v" => WallpaperKind.Video,
            _ => WallpaperKind.Image
        };
    }
}

public record FenceConfig
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; } = 180;
    public double H { get; init; } = 120;
    public bool Folded { get; init; }
    /// <summary>M3：归属屏持久 ID；空串=主屏（旧 config 兼容，启动时归到当前主屏，保存后自然迁移为具体 ID）。</summary>
    public string MonitorId { get; init; } = "";
    public IReadOnlyList<string> IconFilePaths { get; init; } = Array.Empty<string>();
}
