using DesktopManager.Core.Services;
using Microsoft.Win32;
using Serilog;

namespace DesktopManager.Native;

/// <summary>
/// I-3 自清理的注册表读写实现：在 HKCU RunOnce 键下写/删 <see cref="RunOnceSelfCleanupSpec.ValueName"/>。
///
/// 时序契约（见 brief 场景核对）：
/// - app 接管后调 <see cref="SetSelfCleanupOnExit"/> 写 RunOnce（= 启动 app --restore-icons 模式命令）；下次登录
///   Windows 自动执行该命令 → app 调 ShowDesktopIcons（含 WM_SETTINGCHANGE 广播）恢复 HideIcons=0（消除时序依赖）。
/// - app 正常退出调 <see cref="ClearSelfCleanup"/> 删 RunOnce（RestoreExplorer 已恢复 HideIcons=0，钩子无需保留）。
/// - app 崩溃/被杀（OnExit 未跑）→ RunOnce 保留 → 下次登录自动恢复（兜底核心）。
///
/// 所有注册表操作 try/catch 兜底：权限不足/键不存在不致 app 崩溃（I-3 是增强项，不能反过来拖垮主流程）。
/// </summary>
public static class RunOnceSelfCleanup
{
    /// <summary>写 RunOnce 兜底值（覆盖式）。接管后调；每次启动重写以保证最新命令（场景4）。
    /// RunOnce 值内容 = 启动 <c>app.exe --restore-icons</c>（appPath 来自 <see cref="Environment.ProcessPath"/>）。</summary>
    public static void SetSelfCleanupOnExit()
    {
        try
        {
            var appPath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(appPath))
            {
                // 理论兜底：ProcessPath 在某些托管启动场景可能为 null。无路径无法构造 --restore-icons 命令 → 跳过。
                Log.Warning("SetSelfCleanupOnExit：Environment.ProcessPath 为空，无法构造 --restore-icons 命令，跳过 RunOnce 写入（I-3 兜底未生效）。");
                return;
            }
            using var key = Registry.CurrentUser.CreateSubKey(RunOnceSelfCleanupSpec.RunOnceKeyPath, writable: true);
            key.SetValue(RunOnceSelfCleanupSpec.ValueName,
                RunOnceSelfCleanupSpec.BuildRestoreCommand(appPath),
                RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            // 权限丢失/键损坏：不崩 app（内部兜底不阻断启动）。但 I-3 兜底未生效 = 若此时 app 崩溃 →
            // 下次登录桌面图标可能仍空（无 RunOnce 触发 --restore-icons）。真机验收用 reg query 确认 RunOnce 真写入。
            Log.Error(ex, "SetSelfCleanupOnExit 失败（I-3 兜底未生效，若崩溃下次登录桌面可能空）");
        }
    }

    /// <summary>删 RunOnce 兜底值（正常退出调）。键/值不存在视为已清理，不抛。</summary>
    public static void ClearSelfCleanup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunOnceSelfCleanupSpec.RunOnceKeyPath, writable: true);
            if (key is null) return; // 键不存在 = 没写过 = 已干净
            key.DeleteValue(RunOnceSelfCleanupSpec.ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClearSelfCleanup 失败");
        }
    }
}
