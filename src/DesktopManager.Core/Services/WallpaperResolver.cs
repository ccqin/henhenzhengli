using DesktopManager.Core.Models;

namespace DesktopManager.Core.Services;

/// <summary>壁纸解析纯函数（M6 从 MultiMonitorHost 下沉，可单测）：
/// 优先级：有壁纸的显示组（成员屏）&gt; 独立壁纸 &gt; null；组模式画布计算。</summary>
public static class WallpaperResolver
{
    /// <summary>解析某屏生效壁纸与命中组。</summary>
    public static (WallpaperConfig? Cfg, DisplayGroup? Group) Resolve(
        string monitorId,
        IReadOnlyList<WallpaperConfig> wallpapers,
        IReadOnlyList<DisplayGroup> groups)
    {
        var g = groups.FirstOrDefault(g =>
            !string.IsNullOrWhiteSpace(g.WallpaperPath) && g.MonitorIds.Contains(monitorId));
        if (g is not null)
            return (new WallpaperConfig { MonitorId = monitorId, Kind = g.WallpaperKind, Path = g.WallpaperPath }, g);
        return (wallpapers.FirstOrDefault(w => string.Equals(w.MonitorId, monitorId, StringComparison.Ordinal)), null);
    }

    /// <summary>组模式虚拟画布：组内在线成员 ≥2 时返回 (画布, 本屏 rect)；否则 null（降级单屏）。
    /// screens：在线成员屏 (屏ID, rect)。</summary>
    public static (IntRect Canvas, IntRect MonRect)? CalcCanvas(
        string monitorId,
        IReadOnlyList<(string Id, IntRect Rect)> screens)
    {
        if (screens.Count < 2) return null;
        var me = screens.FirstOrDefault(s => s.Id == monitorId);
        if (me.Id is null && me.Rect is null && !screens.Any(s => s.Id == monitorId)) return null;
        var canvas = CrossScreenLayout.Canvas(screens.Select(s => s.Rect).ToList());
        return (canvas, me.Rect);
    }
}
