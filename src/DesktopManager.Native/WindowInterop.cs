using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopManager.Native;

/// <summary>窗口样式 P/Invoke 封装：供 WallpaperWindow / IconLayerWindow 共用置底、点击穿透、不抢焦点等行为。</summary>
public static class WindowInterop
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    // 64-bit safe variants (x64 用 PtrW，x86 用 W)
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll")]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT2 r);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT2 { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetCurrentThreadId();

    public static long GetExtendedStyle(IntPtr hWnd)
        => (IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, GWL_EXSTYLE) : new IntPtr(GetWindowLong32(hWnd, GWL_EXSTYLE))).ToInt64();

    public static void SetExtendedStyle(IntPtr hWnd, long value)
    {
        // SetWindowLong(Ptr) 成功时不清 last-error slot，必须用「返回值为 0 且 error 非 0」双条件判失败，
        // 否则 WPF 内部调用的残留错误码会让成功路径误抛 Win32Exception。返回 0 也可能是合法（前值就是 0）。
        int err;
        if (IntPtr.Size == 8)
        {
            IntPtr prev = SetWindowLongPtr64(hWnd, GWL_EXSTYLE, new IntPtr(value));
            err = prev == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        }
        else
        {
            int prev = SetWindowLong32(hWnd, GWL_EXSTYLE, (int)value);
            err = prev == 0 ? Marshal.GetLastWin32Error() : 0;
        }
        if (err != 0) throw new Win32Exception(err);
    }

    public static void SendToBottom(IntPtr hWnd)
    {
        if (!SetWindowPos(hWnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>M4：壁纸窗嵌入桌面层（Wallpaper Engine 同款）：SetParent 到 Progman 成为子窗，
    /// 排在 Progman 子窗最底（SHELLDLL_DefView 之下）。收益：① shell 永不把桌面内容当全屏 app
    /// （真机：GIF 连续渲染触发全屏检测 → 任务栏被剥 topmost + 壁纸窗顶高盖任务栏）；② 桌面层天然在
    /// 所有顶层窗（含图标层/任务栏）之下，Z-order 零编排。坐标转成 Progman 客户区（虚拟屏）相对坐标。
    /// WS_EX_TRANSPARENT 让 hit-test 穿透到下层桌面（右键桌面菜单保留）。</summary>
    private const int GWL_STYLE = -16;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_CHILD = 0x40000000L;

    public static void AttachToDesktopLayer(IntPtr hWnd, int monX, int monY, int monW, int monH)
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(0, "Progman not found");
        SetParent(hWnd, progman);
        // SetParent 不自动改样式（MSDN 要求调用者重置）：去 WS_POPUP 加 WS_CHILD，
        // 否则「有 parent 的顶层样式窗」DWM 不合成（真机：窗口存在 vis=True 但不绘制）。
        long st = GetStyle(hWnd);
        SetStyle(hWnd, (st & ~WS_POPUP) | WS_CHILD);
        GetWindowRect(progman, out var pr);
        // 子窗坐标 = 虚拟屏坐标 - Progman 客户区原点；HWND_BOTTOM = Progman 子窗最底（DefView 之下）
        SetWindowPos(hWnd, HWND_BOTTOM, monX - pr.Left, monY - pr.Top, monW, monH, 0);
        long ex = GetExtendedStyle(hWnd);
        SetExtendedStyle(hWnd, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
    }

    private static long GetStyle(IntPtr hWnd)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, GWL_STYLE).ToInt64() : GetWindowLong32(hWnd, GWL_STYLE);

    private static void SetStyle(IntPtr hWnd, long value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, GWL_STYLE, new IntPtr(value));
        else SetWindowLong32(hWnd, GWL_STYLE, (int)value);
    }

    /// <summary>M4：桌面层子窗重定位（分辨率/排列变化）。坐标同 <see cref="AttachToDesktopLayer"/>。</summary>
    public static void RepositionDesktopLayer(IntPtr hWnd, int monX, int monY, int monW, int monH)
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return;
        GetWindowRect(progman, out var pr);
        const uint SWP_NOZORDER = 0x0004; // 保持桌面层内 Z（已在最底）
        SetWindowPos(hWnd, IntPtr.Zero, monX - pr.Left, monY - pr.Top, monW, monH, SWP_NOZORDER);
    }

    // ---------- M6：WorkerW 桌面层（Lively 方案） ----------

    private const uint WM_SPAWNWORKERW = 0x052C;
    private const uint SMTO_NORMAL = 0x0;

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    /// <summary>M6：生成并定位壁纸层 WorkerW（Lively 同款）。发送 0x052C 让 shell 确保 WorkerW 存在。
    /// explorer 两种稳定结构都要认（真机：重启后 DefView 常驻 Progman，壁纸 WorkerW 是 Progman 子序中
    /// DefView 之后的那个；若 0x052C 新建了顶层 WorkerW，则 DefView 会被移过去）。</summary>
    public static IntPtr SetupDesktopLayer()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;
        SendMessageTimeout(progman, WM_SPAWNWORKERW, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);

        // ① 常见稳定态：DefView 在 Progman 下 → 壁纸 WorkerW = Progman 子序中 DefView 之后
        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
        {
            var inner = FindWindowEx(progman, defView, "WorkerW", null);
            if (inner != IntPtr.Zero) return inner;
        }

        // ② 0x052C 新生态：DefView 被移到顶层 WorkerW 下 → 顶层 Z 序 Progman 之后的 WorkerW
        var topWorker = FindWindowEx(IntPtr.Zero, progman, "WorkerW", null);
        if (topWorker != IntPtr.Zero) return topWorker;

        // ③ 兜底：DefView 直接挂在某顶层 WorkerW 下（部分 shell 结构）
        if (defView == IntPtr.Zero)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) return found;
        }
        return progman;
    }

    private const int GWL_HWNDPARENT = -8;

    /// <summary>找桌面图标视图窗口 SHELLDLL_DefView（owner 挂载目标）。</summary>
    public static IntPtr GetShellDefView()
    {
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            var dv = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (dv != IntPtr.Zero) return dv;
        }
        // 备选结构：DefView 在某顶层 WorkerW 下
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        if (found != IntPtr.Zero)
            return FindWindowEx(found, IntPtr.Zero, "SHELLDLL_DefView", null);
        return IntPtr.Zero;
    }

    /// <summary>M6 终态（真机结论 2026-08-18，Layouter/Fences 同款技巧）：
    /// 把窗口 Owner 设为 SHELLDLL_DefView（桌面图标视图）。owned 窗口不被 Win+D/ShowDesktop
    /// 最小化（跟随 shell owner）且 Z 序天然贴桌面层；owner 是顶层关系非 SetParent 父子，
    /// 无跨进程渲染问题（本机 SetParent 桌面层物理输出失效的坑完全绕开）。</summary>
    public static void AttachTopLevel(IntPtr hWnd, int monX, int monY, int monW, int monH, bool iconLayer = false)
    {
        var defView = GetShellDefView();
        if (defView != IntPtr.Zero)
            SetWindowLongPtr64(hWnd, GWL_HWNDPARENT, defView);
        long ex = GetExtendedStyle(hWnd);
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (!iconLayer) ex |= WS_EX_TRANSPARENT; // 壁纸点击穿透
        SetExtendedStyle(hWnd, ex);
        SetWindowPos(hWnd, IntPtr.Zero, monX, monY, monW, monH, SWP_NOACTIVATE);
    }

    /// <summary>M6：把子进程窗口 SetParent 到 Progman 成为桌面子窗口。
    /// 真机结论（2026-08）：WorkerW 方案在本机物理输出失效（DWM 截图缓冲有内容但显示器不输出，
    /// 顶层窗口实验证实渲染链正常）→ 弃用 WorkerW，复刻 2110f61 真机验证过的 Progman 方案：
    /// 壁纸 = HWND_BOTTOM + 去 LAYERED + TRANSPARENT 点击穿透；图标层 = HWND_TOPMOST 压 DefView + 去 LAYERED。</summary>
    public static void AttachToDesktop(IntPtr hWnd, int monX, int monY, int monW, int monH,
        bool iconLayer = false)
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(0, "Progman not found");
        SetParent(hWnd, progman);
        long st = GetStyle(hWnd);
        SetStyle(hWnd, (st & ~WS_POPUP) | WS_CHILD);
        GetWindowRect(progman, out var pr);
        int cx = monX - pr.Left, cy = monY - pr.Top;
        var after = iconLayer ? HWND_TOPMOST : HWND_BOTTOM;
        SetWindowPos(hWnd, after, cx, cy, monW, monH, iconLayer ? SWP_NOACTIVATE : 0);
        long ex = GetExtendedStyle(hWnd);
        // 去 WS_EX_LAYERED：child 形态下 Layered 表面渲染失效（2110f61 真机教训）。
        ex &= ~WS_EX_LAYERED;
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        if (!iconLayer) ex |= WS_EX_TRANSPARENT; // 壁纸点击穿透
        SetExtendedStyle(hWnd, ex);
    }

    /// <summary>顶层形态重定位（拓扑变化）。屏幕坐标直接定位。</summary>
    public static void RepositionDesktopChild(IntPtr hWnd, int monX, int monY, int monW, int monH)
    {
        const uint SWP_NOZORDER = 0x0004;
        SetWindowPos(hWnd, IntPtr.Zero, monX, monY, monW, monH, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>取桌面子窗口挂载目标 Progman（跨进程 SetParent 用）。</summary>
    public static IntPtr GetProgman() => FindWindow("Progman", null);


    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);

    private const long WS_EX_TOPMOST = 0x00000008;
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "DV2ControlHost"
    };

    /// <summary>M5 修闪：检测自己的窗口是否浮高——Z 序（顶→底）中若存在
    /// 「普通外部窗」（可见、非 topmost、非 shell 类）位于自己窗口**下方**，即自己浮到了普通窗口之上。
    /// 正常底置态：普通外部窗都在自己上方，不触发。</summary>
    public static bool DetectOwnFloating(IReadOnlyList<IntPtr> ownHwnds)
    {
        var order = new List<IntPtr>();
        EnumWindows((h, _) => { order.Add(h); return true; }, IntPtr.Zero);
        var own = new HashSet<IntPtr>(ownHwnds);

        int firstOwn = order.FindIndex(h => own.Contains(h));
        if (firstOwn < 0) return false;

        for (int i = firstOwn + 1; i < order.Count; i++)
        {
            var h = order[i];
            if (own.Contains(h)) continue;
            if (!IsWindowVisible(h)) continue;
            if ((GetExtendedStyle(h) & WS_EX_TOPMOST) != 0) continue;
            var sb = new StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            if (ShellClasses.Contains(sb.ToString())) continue;
            return true; // 普通外部窗在自己下方 = 自己浮高
        }
        return false;
    }

    /// <summary>M4：把 <paramref name="hwnd"/> 精确插到 <paramref name="aboveHwnd"/> 正下方
    /// （hWndInsertAfter=aboveHwnd → 它在本窗之上）。壁纸窗置底于本屏图标层用，不靠创建顺序赌 Z-order。</summary>
    public static void PlaceBelow(IntPtr hwnd, IntPtr aboveHwnd)
    {
        if (!SetWindowPos(hwnd, aboveHwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>点击穿透样式（不置底）：WS_EX_LAYERED|TRANSPARENT|NOACTIVATE。
    /// 注：Win11 DWM 会对非透明 WPF 窗口抹掉 WS_EX_LAYERED（真机 ex 观察），无害——穿透靠 TRANSPARENT。</summary>
    public static void ApplyClickThroughStyles(IntPtr hWnd)
    {
        long ex = GetExtendedStyle(hWnd);
        SetExtendedStyle(hWnd, ex | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
    }

    /// <summary>壁纸层：点击穿透（WS_EX_LAYERED|TRANSPARENT|NOACTIVATE）+ 置底。</summary>
    public static void MakeClickThrough(IntPtr hWnd)
    {
        ApplyClickThroughStyles(hWnd);
        SendToBottom(hWnd);
    }

    /// <summary>图标层：不点击穿透但同样不抢焦点（WS_EX_LAYERED|NOACTIVATE，去 TRANSPARENT）。M1 IconLayerWindow 用。</summary>
    public static void MakeNonInteractiveTopmost(IntPtr hWnd)
    {
        long ex = GetExtendedStyle(hWnd);
        SetExtendedStyle(hWnd, ex | WS_EX_LAYERED | WS_EX_NOACTIVATE);
        SendToBottom(hWnd);
    }

    // ---------- M2 真机修复 Bug 2：NOACTIVATE 临时激活 ----------
    // 背景：IconLayerWindow 设 WS_EX_NOACTIVATE（桌面图标层不抢 explorer 焦点）→ app 进程从不获取前台
    // → 所有 TextBox（FenceControl.TitleEdit / RenameDialog.Input）无法接收键盘输入。
    // 不能直接去 NOACTIVATE（全屏透明窗口会浮到所有窗口前覆盖一切）。方案：仅在需要键盘输入时
    // 临时去 NOACTIVATE + SetForegroundWindow；输入结束恢复 NOACTIVATE + SendToBottom 回桌面层 Z-order。
    // 调用方必须保证 EnableActivation / RestoreNonInteractive 严格成对（try/finally），否则 IconLayer
    // 激活后回不到桌面层（浮在所有窗口前）。

    /// <summary>去掉 WS_EX_NOACTIVATE 并尝试把窗口设到前台（让 app 进程获得输入焦点）。
    /// 返回之前的 ex style（含 NOACTIVATE），供 RestoreNonInteractive 恢复。
    /// 内部用 AttachThreadInput 兜底：NOACTIVATE 进程调 SetForegroundWindow 常被前台锁定拒绝，
    /// AttachThreadInput 把当前前台线程的 input 队列临时挂到本线程，绕过 SetForegroundWindow 的前厣检查。</summary>
    public static long EnableActivation(IntPtr hWnd)
    {
        long prev = GetExtendedStyle(hWnd);
        if ((prev & WS_EX_NOACTIVATE) != 0)
        {
            SetExtendedStyle(hWnd, prev & ~WS_EX_NOACTIVATE);
        }
        ForceForeground(hWnd);
        return prev;
    }

    /// <summary>M6 owner 形态：仅恢复 ex 样式（含 NOACTIVATE），不动 Z 序——
    /// owned 窗口 SendToBottom 会压破 owned 约束进桌面层之下，图标层"全消失"（真机踩坑：
    /// 系统菜单弹出的前台化恢复曾调 RestoreNonInteractive→SendToBottom）。Z 序由 owner 关系天然约束。</summary>
    public static void RestoreNoActivateStyle(IntPtr hWnd, long prevEx)
    {
        try { SetExtendedStyle(hWnd, prevEx); }
        catch { /* 窗口已无效 */ }
    }

    /// <summary>恢复 NOACTIVATE（用 EnableActivation 返回的 prevEx）并 SendToBottom 回桌面层 Z-order。
    /// 必须 EnableActivation/RestoreNonInteractive 成对调用（try/finally 包裹）。prevEx 含原 NOACTIVATE。</summary>
    public static void RestoreNonInteractive(IntPtr hWnd, long prevEx)
    {
        // prevEx 是 EnableActivation 抓取的快照（含 NOACTIVATE），直接 set 回去最安全。
        // 多次调用幂等：当前 ex 已等于 prevEx 时 SetExtendedStyle 不会改变行为（Win32 Set 调用）。
        try
        {
            SetExtendedStyle(hWnd, prevEx);
        }
        catch (Win32Exception)
        {
            // 极端场景（窗口已关闭等）不应阻塞调用方 finally。降级：仅尝试重 |NOACTIVATE，再失败就忽略。
            try { SetExtendedStyle(hWnd, GetExtendedStyle(hWnd) | WS_EX_NOACTIVATE); }
            catch { /* 窗口已无效，无法恢复，静默 */ }
        }
        try { SendToBottom(hWnd); }
        catch (Win32Exception) { /* 同上：不阻塞 finally */ }
    }


    /// <summary>让 hWnd 进入前台。AttachThreadInput trick：把当前前台窗口的线程 input 队列 attach 到本线程，
    /// 使 SetForegroundWindow 通过（绕过「非前台进程不能 SetForeground」限制），随后立即 detach。</summary>
    private static void ForceForeground(IntPtr hWnd)
    {
        var fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint curThread = GetCurrentThreadId();
        bool attached = false;
        if (fgThread != 0 && fgThread != curThread)
        {
            attached = AttachThreadInput(curThread, fgThread, true);
        }
        try
        {
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached) AttachThreadInput(curThread, fgThread, false);
        }
    }
}
