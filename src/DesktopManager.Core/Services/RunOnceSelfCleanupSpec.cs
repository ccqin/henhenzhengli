namespace DesktopManager.Core.Services;

/// <summary>
/// I-3 自清理（RunOnce 兜底）的**纯数据规约**：RunOnce 键路径、值名、reg.exe 恢复命令。
/// 放 Core（net10.0，无 Windows 依赖）便于单测；实际注册表读写在 <c>DesktopManager.Native.RunOnceSelfCleanup</c>。
///
/// 兜底语义：app 接管 explorer 桌面图标（设 HideIcons=1）后若崩溃且不再启动，RunOnce 值保留 →
/// 用户下次登录时 Windows 自动执行 RunOnce 命令（启动 app --restore-icons 模式）→ app 调 ShowDesktopIcons
/// （含 WM_SETTINGCHANGE 广播）把 HideIcons 恢复为 0 → 桌面图标回来（I-3 致命项，时序安全）。
/// </summary>
public static class RunOnceSelfCleanupSpec
{
    /// <summary>RunOnce 键路径（HKCU 基根由 <c>Registry.CurrentUser</c> 提供）。
    /// 该键下的值在用户下次登录时由 Windows 自动执行一次，执行后自动删除。</summary>
    public const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

    /// <summary>RunOnce 下用于自清理的值名。</summary>
    public const string ValueName = "DM_RestoreIcons";

    /// <summary>
    /// explorer 桌面图标显隐的注册表键路径（与 <c>DesktopIconVisibility</c> 同目标）。
    /// **含 <c>HKCU\</c> 根键前缀**：文档用途——描述 <c>DesktopIconVisibility</c> 最终作用的注册表目标
    /// （Native 层用无前缀形式 + <c>Registry.CurrentUser</c> 基根；此处 HKCU 前缀形式用于人类可读说明）。
    /// </summary>
    public const string AdvancedKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    /// <summary>HideIcons 值名（文档用途：RestoreExplorer 最终改写的值名）。</summary>
    public const string HideIconsValueName = "HideIcons";

    /// <summary>恢复值：0=显示桌面图标（HideIcons 的 explorer 语义）。</summary>
    public const int HideIconsRestoreValue = 0;

    /// <summary>
    /// 构造 RunOnce 值内容：启动 app 的 <c>--restore-icons</c> 模式命令。
    ///
    /// 闭环（I-3 致命项时序安全）：app 接管后写此 RunOnce → 崩溃未正常退出 → 下次登录 Windows 执行该命令 →
    /// 启动 <c>app.exe --restore-icons</c> → app 调 <c>RecoveryGuard.RestoreExplorer()</c>
    /// （= <c>DesktopIconVisibility.ShowDesktopIcons()</c>，**含 WM_SETTINGCHANGE 广播**）→ HideIcons=0 且广播刷新
    /// explorer → 桌面图标恢复 → app 退出（不接管、不建窗口）。
    ///
    /// **为何用 app 而非 reg.exe**：reg.exe 只改注册表**不广播 WM_SETTINGCHANGE**。崩溃恢复场景 HideIcons=1，
    /// 若登录时 explorer 先读 1、reg.exe 后改 0 但不广播 → 本次登录桌面仍空（致命）。
    /// --restore-icons 模式调 <c>ShowDesktopIcons</c> 含广播 → **无论 explorer 是否已读 HideIcons=1 都会刷新** → 消除 RunOnce/explorer 时序依赖。
    ///
    /// 路径用引号包裹（防空格，如 Program Files）。稳定字符串：每次启动覆盖式重写（场景4），幂等。
    /// </summary>
    /// <param name="appPath">app.exe 完整路径（通常 <c>Environment.ProcessPath</c>）。</param>
    public static string BuildRestoreCommand(string appPath) =>
        $"\"{appPath}\" --restore-icons";
}
