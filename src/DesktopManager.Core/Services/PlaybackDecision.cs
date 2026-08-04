namespace DesktopManager.Core.Services;

/// <summary>M4-T1：壁纸播放暂停决策（纯函数，可单测）。
/// 三个暂停条件取或（安全方向：误判只导致暂停，不会在用户不看时继续耗电）。
/// App 层 PlaybackGovernor 负责采集三个输入（全屏检测/电源/锁屏），本类不碰 Win32。</summary>
public static class PlaybackDecision
{
    /// <param name="fullScreenApp">前台有覆盖整屏的应用（游戏/视频等，此时壁纸不可见）</param>
    /// <param name="onBattery">电池供电（省电优先）</param>
    /// <param name="sessionLocked">会话已锁屏（Win+L 等）</param>
    /// <returns>true = 应暂停播放。</returns>
    public static bool ShouldPause(bool fullScreenApp, bool onBattery, bool sessionLocked)
        => fullScreenApp || onBattery || sessionLocked;
}
