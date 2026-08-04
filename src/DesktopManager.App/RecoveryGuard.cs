using DesktopManager.Core.Services;
using DesktopManager.Native;

namespace DesktopManager.App;

/// <summary>
/// 接管 explorer 桌面图标的薄壳：把 M0 的 DesktopIconVisibility（注册表 HideIcons）
/// 包成 <see cref="Func{T}"/>/<see cref="Action{T}"/> 回调注入 Core 的 <see cref="RecoveryStateDetector"/>。
/// 所有纯状态逻辑在 Core（已被单测覆盖）；本类只负责 Native 桥接。
/// </summary>
public sealed class RecoveryGuard
{
    private readonly RecoveryStateDetector _detector;

    public RecoveryGuard()
    {
        _detector = new RecoveryStateDetector(
            isHidden: DesktopIconVisibility.IsHidden,
            setHidden: v =>
            {
                if (v) DesktopIconVisibility.HideDesktopIcons();
                else DesktopIconVisibility.ShowDesktopIcons();
            });
    }

    /// <summary>启动检测：若 HideIcons==1 表示上次接管过（可能崩溃，正常退出会恢复）。</summary>
    public RecoveryState DetectState() => _detector.Detect();

    /// <summary>接管：隐藏 explorer 原生桌面图标。</summary>
    public void TakeOver() => _detector.TakeOver();

    /// <summary>恢复：让 explorer 原生桌面图标重新显示（正常退出调）。</summary>
    public void RestoreExplorer() => _detector.RestoreExplorer();

    /// <summary>explorer 重启后恢复（--restore-icons 专用）：绕过 detector 直接调
    /// ShowDesktopIconsAfterShellRestart（重刷注册表 + SW_SHOW 新 ListView），
    /// 对策垂死 explorer 冲刷 HideIcons=1 的真机问题。</summary>
    public void RestoreExplorerAfterShellRestart() => DesktopIconVisibility.ShowDesktopIconsAfterShellRestart();

    /// <summary>写 RunOnce 自清理兜底（I-3）：接管后调，崩溃未正常退出时下次登录自动恢复 HideIcons=0。</summary>
    public void SetSelfCleanupOnExit() => RunOnceSelfCleanup.SetSelfCleanupOnExit();

    /// <summary>清 RunOnce 自清理兜底（I-3）：正常退出调（RestoreExplorer 已恢复，钩子无需保留）。</summary>
    public void ClearSelfCleanup() => RunOnceSelfCleanup.ClearSelfCleanup();
}
