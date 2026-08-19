namespace DesktopManager.Core.Models;

/// <summary>整数矩形（虚拟屏坐标，左闭右开语义用 Right/Bottom Exclusive 计算宽高）。
/// Core 纯数据类型：跨屏裁剪（M5）与排列拖拽（M5-T5）共用，不依赖 WPF。</summary>
public record IntRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;


    /// <summary>边接触（共享边段：左右贴合且有垂直重叠，或上下贴合且有水平重叠）= 拓扑连通。</summary>
    public bool EdgeTouches(IntRect o)
    {
        var vOverlap = Math.Min(Bottom, o.Bottom) - Math.Max(Top, o.Top) > 0;
        var hOverlap = Math.Min(Right, o.Right) - Math.Max(Left, o.Left) > 0;
        return (Right == o.Left || Left == o.Right) && vOverlap
            || (Bottom == o.Top || Top == o.Bottom) && hOverlap;
    }
}

/// <summary>M5：显示组——组内屏共享一个壁纸源（跨屏拼接/视频同步）。
/// MonitorIds = 成员屏持久 ID；WallpaperPath 空串 = 组无壁纸（成员回退独立壁纸）。</summary>
public record DisplayGroup
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public IReadOnlyList<string> MonitorIds { get; init; } = Array.Empty<string>();
    public WallpaperKind WallpaperKind { get; init; } = WallpaperKind.Image;
    public string WallpaperPath { get; init; } = "";
}
