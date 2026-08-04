using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DesktopManager.Native;

/// <summary>隐藏/恢复 explorer 原生桌面图标显示（等价于桌面右键→查看→显示桌面图标）。
/// 双机制：① 注册表 HideIcons + WM_SETTINGCHANGE 广播（持久化状态，explorer 重启后仍生效，
/// RecoveryStateDetector 也读它做崩溃检测）；② 直接 ShowWindow 桌面图标 SysListView32
/// （立即生效——Win11 上 explorer 不响应广播重读 HideIcons，仅靠①图标不消失，真机已确认）。</summary>
public static class DesktopIconVisibility
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ValueName = "HideIcons";

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, UIntPtr wParam, string? lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private const uint WM_SETTINGCHANGE = 0x001A;
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const uint GW_HWNDNEXT = 2;

    /// <summary>隐藏原生桌面图标。注册表+广播（持久化）失败不阻塞；ListView 直接隐藏（立即生效）。</summary>
    public static void HideDesktopIcons()
    {
        SetHidden(true);
        SetListViewVisible(false);
    }

    /// <summary>恢复原生桌面图标。注册表+广播（持久化）+ ListView 重新显示。</summary>
    public static void ShowDesktopIcons()
    {
        SetHidden(false);
        SetListViewVisible(true);
    }

    // ---------- 机制②：直接隐藏桌面图标 ListView（Win11 必需） ----------
    // Win11 上 explorer 不响应 WM_SETTINGCHANGE 重读 HideIcons（真机：注册表已写 1 但图标仍显示）。
    // 直接 ShowWindow(SW_HIDE) 桌面图标 ListView（SysListView32）立即生效，无需重启 explorer。
    // 层级：Progman → SHELLDLL_DefView → SysListView32；Win7+ DefView 也可能在 Progman 之后的
    // WorkerW 里（壁纸层切换导致），两种位置都找。
    // explorer 重启后 ListView 重建会重新显示 → ShellRestartWatcher → TakeOver 再次调本方法兜底。

    private static void SetListViewVisible(bool visible)
    {
        IntPtr lv = FindDesktopListView();
        if (lv == IntPtr.Zero) return; // explorer 未就绪/窗口结构异常：注册表机制①已生效，等 explorer 重启重读
        ShowWindow(lv, visible ? SW_SHOW : SW_HIDE);
    }

    private static IntPtr FindDesktopListView()
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        // 位置 1：Progman → SHELLDLL_DefView → SysListView32（经典布局）
        IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            // 位置 2：Win7+ 壁纸切换后 DefView 迁到 Progman 之后的 WorkerW 顶层窗口里。
            // 从 Progman 起向后遍历兄弟窗口，找第一个含 SHELLDLL_DefView 子窗口的。
            IntPtr sibling = progman;
            while ((sibling = GetWindow(sibling, GW_HWNDNEXT)) != IntPtr.Zero)
            {
                var cls = new StringBuilder(64);
                GetClassName(sibling, cls, cls.Capacity);
                if (cls.ToString() != "WorkerW") continue;
                defView = FindWindowEx(sibling, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero) break;
            }
        }
        if (defView == IntPtr.Zero) return IntPtr.Zero;
        return FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    /// <summary>读取当前 HideIcons 注册表值。true=已隐藏（接管中），false=未隐藏或键不存在。
    /// 仅反映注册表（持久化状态）；ListView 显隐是运行期即时效果，不参与状态判定。</summary>
    public static bool IsHidden()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
        return key?.GetValue(ValueName) is int v && v != 0;
    }

    private static void SetHidden(bool hidden)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AdvancedKey, writable: true);
        key.SetValue(ValueName, hidden ? 1 : 0, RegistryValueKind.DWord);
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero,
            "Shell", SMTO_ABORTIFHUNG, 1000, out _);
    }
}
