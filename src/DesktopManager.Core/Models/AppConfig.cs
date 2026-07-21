namespace DesktopManager.Core.Models;

public record AppConfig(
    bool HideExplorerIcons = false,
    bool AutoStart = true,
    IReadOnlyList<FenceConfig> Fences = null!);

public record FenceConfig(string Id, string Title, int X, int Y, int W, int H);
