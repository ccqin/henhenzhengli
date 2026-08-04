namespace DesktopManager.Core.Models;

public record AppConfig
{
    public bool HideExplorerIcons { get; init; } = false;
    public bool AutoStart { get; init; } = true;
    public IReadOnlyList<FenceConfig> Fences { get; init; } = Array.Empty<FenceConfig>();
    // 散落图标自由摆放位置（自由摆放特性）：重启后保持用户拖到的位置。
    // 仅记录散落（非 Fence 归属）图标；Fence 内图标位置由 FenceConfig.IconFilePaths 隐含。
    public IReadOnlyList<IconPosition> IconPositions { get; init; } = Array.Empty<IconPosition>();
}

/// <summary>散落图标自由摆放位置持久化记录。FilePath 为键（OrdIgnoreCase），X/Y 为 LooseItemsControl 坐标系（与 IconCanvas 1:1）。
/// M3：MonitorId = 归属屏持久 ID（窗口本地坐标系）；空串=主屏（旧 config 兼容）。</summary>
public record IconPosition(string FilePath, double X, double Y, string MonitorId = "");

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
