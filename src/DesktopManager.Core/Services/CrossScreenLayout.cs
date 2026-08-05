using DesktopManager.Core.Models;

namespace DesktopManager.Core.Services;

/// <summary>M5-T1：跨屏壁纸几何（纯函数，可单测）。
/// 虚拟画布 = 组成员显示器 rect 的 bounding box；源图按 **cover**（等比缩放铺满画布、
/// 超出居中裁掉）映射到画布；每屏裁剪 rect = 本屏与画布交集在源图像素坐标的对应区域。</summary>
public static class CrossScreenLayout
{
    /// <summary>组成员 rect 的 bounding box；空集合返回 null。</summary>
    public static IntRect? Canvas(IReadOnlyList<IntRect> monitors)
    {
        if (monitors.Count == 0) return null;
        var l = monitors.Min(m => m.Left);
        var t = monitors.Min(m => m.Top);
        var r = monitors.Max(m => m.Right);
        var b = monitors.Max(m => m.Bottom);
        return new IntRect(l, t, r, b);
    }

    /// <summary>本屏应渲染的源图像素区域（cover 语义，居中裁切）。
    /// 返回整数像素（x 向下取整、w 向上取整后 clamp 到源图边界，接缝容错方向：宁可重叠 1px 不留缝）。</summary>
    public static (int X, int Y, int W, int H) CropRect(int bitmapW, int bitmapH, IntRect canvas, IntRect monitor)
    {
        // cover 缩放：1 源像素 = scale 画布单位
        double scale = Math.Max((double)canvas.Width / bitmapW, (double)canvas.Height / bitmapH);
        // 缩放后图比画布大的部分居中裁掉（画布单位）
        double offsetX = (bitmapW * scale - canvas.Width) / 2;
        double offsetY = (bitmapH * scale - canvas.Height) / 2;

        // 本屏相对画布 → 源像素（含居中偏移）
        double px = (monitor.Left - canvas.Left + offsetX) / scale;
        double py = (monitor.Top - canvas.Top + offsetY) / scale;
        double pw = monitor.Width / scale;
        double ph = monitor.Height / scale;

        int x = (int)Math.Floor(px);
        int y = (int)Math.Floor(py);
        int w = (int)Math.Ceiling(px + pw) - x;
        int h = (int)Math.Ceiling(py + ph) - y;

        // clamp 到源图边界
        x = Math.Clamp(x, 0, bitmapW - 1);
        y = Math.Clamp(y, 0, bitmapH - 1);
        w = Math.Clamp(w, 1, bitmapW - x);
        h = Math.Clamp(h, 1, bitmapH - y);
        return (x, y, w, h);
    }
}
