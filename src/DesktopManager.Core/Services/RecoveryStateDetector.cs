namespace DesktopManager.Core.Services;

/// <summary>接管状态：Clean=未接管；PreviouslyTakenOver=上次接管过（可能崩溃，因正常退出会恢复）。</summary>
public enum RecoveryState
{
    Clean,
    PreviouslyTakenOver
}

/// <summary>
/// 接管状态机的纯逻辑核心：注入读 HideIcons 的 <see cref="Func{T}"/> 和设 HideIcons 的
/// <see cref="Action{T}"/>，便于单测；App 层 RecoveryGuard 只是把 DesktopIconVisibility 包成这两个回调注入。
/// </summary>
public sealed class RecoveryStateDetector
{
    private readonly Func<bool> _isHidden;
    private readonly Action<bool> _setHidden;

    public RecoveryStateDetector(Func<bool> isHidden, Action<bool> setHidden)
    {
        _isHidden = isHidden;
        _setHidden = setHidden;
    }

    /// <summary>读 HideIcons 当前值：true 视为上次接管过（可能崩溃，正常退出会恢复成 false）。</summary>
    public RecoveryState Detect() => _isHidden() ? RecoveryState.PreviouslyTakenOver : RecoveryState.Clean;

    /// <summary>接管：设 HideIcons=true（隐藏 explorer 原生桌面图标）。</summary>
    public void TakeOver() => _setHidden(true);

    /// <summary>恢复：设 HideIcons=false（让 explorer 原生桌面图标重新显示）。</summary>
    public void RestoreExplorer() => _setHidden(false);
}
