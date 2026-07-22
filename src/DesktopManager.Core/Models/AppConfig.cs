namespace DesktopManager.Core.Models;

public record AppConfig
{
    public bool HideExplorerIcons { get; init; } = false;
    public bool AutoStart { get; init; } = true;
    public IReadOnlyList<FenceConfig> Fences { get; init; } = Array.Empty<FenceConfig>();
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
    public IReadOnlyList<string> IconFilePaths { get; init; } = Array.Empty<string>();
}
