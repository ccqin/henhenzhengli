namespace DesktopManager.Core.Models;

public record AppConfig
{
    public bool HideExplorerIcons { get; init; } = false;
    public bool AutoStart { get; init; } = true;
    public IReadOnlyList<FenceConfig> Fences { get; init; } = Array.Empty<FenceConfig>();
}

public record FenceConfig(string Id, string Title, int X, int Y, int W, int H);
