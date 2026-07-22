using System.Diagnostics;
using DesktopManager.Core.Services;
using Microsoft.Win32;

namespace DesktopManager.Native;

/// <summary>
/// I-3 自清理的注册表读写实现：在 HKCU RunOnce 键下写/删 <see cref="RunOnceSelfCleanupSpec.ValueName"/>。
///
/// 时序契约（见 brief 场景核对）：
/// - app 接管后调 <see cref="SetSelfCleanupOnExit"/> 写 RunOnce；下次登录 Windows 自动执行 reg.exe 恢复 HideIcons=0。
/// - app 正常退出调 <see cref="ClearSelfCleanup"/> 删 RunOnce（RestoreExplorer 已恢复 HideIcons=0，钩子无需保留）。
/// - app 崩溃/被杀（OnExit 未跑）→ RunOnce 保留 → 下次登录自动恢复（兜底核心）。
///
/// 所有注册表操作 try/catch 兜底：权限不足/键不存在不致 app 崩溃（I-3 是增强项，不能反过来拖垮主流程）。
/// </summary>
public static class RunOnceSelfCleanup
{
    /// <summary>写 RunOnce 兜底值（覆盖式）。接管后调；每次启动重写以保证最新命令（场景4）。</summary>
    public static void SetSelfCleanupOnExit()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunOnceSelfCleanupSpec.RunOnceKeyPath, writable: true);
            key.SetValue(RunOnceSelfCleanupSpec.ValueName,
                RunOnceSelfCleanupSpec.BuildRestoreCommand(),
                RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            // 权限丢失/键损坏：不崩 app；记录便于诊断。兜底失败 = 失去 I-3 兜底能力，但主流程仍正常。
            Debug.WriteLine($"SetSelfCleanupOnExit 失败：{ex}");
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
            Debug.WriteLine($"ClearSelfCleanup 失败：{ex}");
        }
    }
}
