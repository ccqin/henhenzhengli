namespace DesktopManager.Core.Services;

/// <summary>
/// I-3 自清理（RunOnce 兜底）的**纯数据规约**：RunOnce 键路径、值名、reg.exe 恢复命令。
/// 放 Core（net10.0，无 Windows 依赖）便于单测；实际注册表读写在 <c>DesktopManager.Native.RunOnceSelfCleanup</c>。
///
/// 兜底语义：app 接管 explorer 桌面图标（设 HideIcons=1）后若崩溃且不再启动，RunOnce 值保留 →
/// 用户下次登录时 Windows 自动执行 reg.exe 把 HideIcons 恢复为 0 → 桌面图标回来（I-3 致命项）。
/// </summary>
public static class RunOnceSelfCleanupSpec
{
    /// <summary>RunOnce 键路径（HKCU 基根由 <c>Registry.CurrentUser</c> 提供）。
    /// 该键下的值在用户下次登录时由 Windows 自动执行一次，执行后自动删除。</summary>
    public const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

    /// <summary>RunOnce 下用于自清理的值名。</summary>
    public const string ValueName = "DM_RestoreIcons";

    /// <summary>
    /// explorer 桌面图标显隐的注册表键路径（与 <c>DesktopIconVisibility</c> 同目标，但形式不同）。
    /// **含 <c>HKCU\</c> 根键前缀**：本常量的唯一消费者是 <see cref="BuildRestoreCommand"/> 产出的 reg.exe 命令字符串，
    /// reg.exe CLI 要求完整路径含根键前缀（无前缀则报「无效的项名称」退出非 0 → I-3 兜底失效 → 桌面永久空，致命）。
    /// 注意：<c>DesktopManager.Native.DesktopIconVisibility</c> 的私有常量是**无前缀**形式（供
    /// <c>Registry.CurrentUser.OpenSubKey</c>，基根由 CurrentUser 提供）；两者语义不同，勿混用。
    /// </summary>
    public const string AdvancedKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    /// <summary>HideIcons 值名。</summary>
    public const string HideIconsValueName = "HideIcons";

    /// <summary>恢复值：0=显示桌面图标（HideIcons 的 explorer 语义）。</summary>
    public const int HideIconsRestoreValue = 0;

    /// <summary>
    /// 构造 RunOnce 值内容：一条 reg.exe 命令，把 HideIcons 恢复为 0。
    /// 登录时 explorer 刚启动，读初始 HideIcons=0 即正常显示图标；reg.exe 改注册表无需额外广播。
    /// 稳定字符串：每次启动覆盖式重写（场景4），幂等。
    /// </summary>
    public static string BuildRestoreCommand() =>
        $"reg.exe add \"{AdvancedKeyPath}\" /v {HideIconsValueName} /t REG_DWORD /d {HideIconsRestoreValue} /f";
}
