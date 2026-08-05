namespace DesktopManager.Core.Services;

/// <summary>M4-T4：全屏覆盖判定（纯函数，可单测）。
/// 前台窗口 rect 完全覆盖显示器 rect = 全屏应用（此时壁纸不可见，应暂停省电）。
/// 最大化窗口 ≠ 全屏：最大化 rect = 工作区（任务栏条不在内），不满足覆盖条件。</summary>
public static class MonitorCoverage
{
    /// <summary>fg 是否覆盖 monitor（边重合算覆盖）。</summary>
    public static bool Covers(
        int fgLeft, int fgTop, int fgRight, int fgBottom,
        int monLeft, int monTop, int monRight, int monBottom)
        => fgLeft <= monLeft && fgTop <= monTop && fgRight >= monRight && fgBottom >= monBottom;
}
