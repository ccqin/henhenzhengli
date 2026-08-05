using DesktopManager.Core.Models;

namespace DesktopManager.Core.Services;

/// <summary>M5-T1（M3-T8 欠账）：屏幕排列拖拽规划（纯函数，可单测）。
/// 输入拖拽中矩形 + 其他屏幕矩形，输出规划后矩形：
/// ① 边缘吸附（阈值 <see cref="SnapThreshold"/>：顶/底对齐、左右贴合/对齐）；
/// ② 重叠推开（沿穿透较浅的轴推回）；
/// ③ 连通钳制（Windows 要求显示拓扑连通：必须与某屏边接触，违反时钳到移动量最小的合法贴合位）。</summary>
public static class ArrangementPlanner
{
    public const int SnapThreshold = 24;

    public static IntRect Plan(IntRect dragged, IReadOnlyList<IntRect> others)
    {
        var r = dragged;

        // 1. 边缘吸附（逐其他矩形，先垂直后水平，独立轴可叠加）
        foreach (var o in others)
        {
            // 垂直：顶/底对齐 或 上下贴合
            if (Math.Abs(r.Top - o.Top) <= SnapThreshold) r = r.Shift(0, o.Top - r.Top);
            else if (Math.Abs(r.Bottom - o.Bottom) <= SnapThreshold) r = r.Shift(0, o.Bottom - r.Bottom);
            else if (Math.Abs(r.Bottom - o.Top) <= SnapThreshold) r = r.Shift(0, o.Top - r.Bottom);
            else if (Math.Abs(r.Top - o.Bottom) <= SnapThreshold) r = r.Shift(0, o.Bottom - r.Top);

            // 水平：左右贴合 或 左/右对齐
            if (Math.Abs(r.Left - o.Right) <= SnapThreshold) r = r.Shift(o.Right - r.Left, 0);
            else if (Math.Abs(r.Right - o.Left) <= SnapThreshold) r = r.Shift(o.Left - r.Right, 0);
            else if (Math.Abs(r.Left - o.Left) <= SnapThreshold) r = r.Shift(o.Left - r.Left, 0);
            else if (Math.Abs(r.Right - o.Right) <= SnapThreshold) r = r.Shift(o.Right - r.Right, 0);
        }

        // 2. 重叠推开：沿穿透较浅的轴推回（穿透 = 重叠深度）
        foreach (var o in others)
        {
            // 防御上限：正常 1 次即推开，上限防异常输入死循环
            for (int guard = 0; guard < 8 && r.IntersectArea(o) > 0; guard++)
            {
                int pushLeft = r.Right - o.Left;    // 把 r 推到 o 左边
                int pushRight = o.Right - r.Left;   // 把 r 推到 o 右边
                int pushUp = r.Bottom - o.Top;
                int pushDown = o.Bottom - r.Top;
                int min = Math.Min(Math.Min(pushLeft, pushRight), Math.Min(pushUp, pushDown));
                if (min == pushLeft) r = r.Shift(-pushLeft, 0);
                else if (min == pushRight) r = r.Shift(pushRight, 0);
                else if (min == pushUp) r = r.Shift(0, -pushUp);
                else r = r.Shift(0, pushDown);
            }
        }

        // 3. 连通钳制：与所有 others 都无边接触 → 钳到移动量最小的贴合位
        if (others.Count > 0 && !others.Any(o => r.EdgeTouches(o)))
        {
            IntRect best = r;
            double bestDist = double.MaxValue;
            foreach (var o in others)
            {
                // 四个贴合候选位（r 的左上角落点），垂直/水平方向对齐 o 的对应边
                var candidates = new[]
                {
                    new IntRect(o.Right, o.Top, o.Right + r.Width, o.Top + r.Height),          // o 右侧
                    new IntRect(o.Left - r.Width, o.Top, o.Left, o.Top + r.Height),            // o 左侧
                    new IntRect(o.Left, o.Bottom, o.Left + r.Width, o.Bottom + r.Height),      // o 下侧
                    new IntRect(o.Left, o.Top - r.Height, o.Left + r.Width, o.Top),            // o 上侧
                };
                foreach (var c in candidates)
                {
                    if (others.Any(x => !ReferenceEquals(x, o) && c.IntersectArea(x) > 0)) continue; // 候选位与第三屏重叠则弃
                    var d = Math.Pow(c.Left - dragged.Left, 2) + Math.Pow(c.Top - dragged.Top, 2);
                    if (d < bestDist) { bestDist = d; best = c; }
                }
            }
            r = best;
        }

        return r;
    }
}
