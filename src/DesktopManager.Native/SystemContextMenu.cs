using System.Runtime.InteropServices;
using DesktopManager.Native;

namespace DesktopManager.Native;

/// <summary>系统 shell 右键菜单（资源管理器同款：打开方式/第三方扩展/属性）。
/// 经典路径：ILCreateFromPath → SHBindToParent 得父 IShellFolder + 子 pidl →
/// GetUIObjectOf(IContextMenu) → QueryContextMenu 生成 HMENU → TrackPopupMenuEx → InvokeCommand。
/// （此前用 IShellItem.BindToHandler 版本因 vtable 声明错位拿到空菜单，已废弃。）</summary>
public static class SystemContextMenu
{
    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO info);
        [PreserveSig] int GetCommandString(uint idCmd, uint uType, uint dwReserved, IntPtr pszName, uint cchMax);
    }

    // 必须与 Win32 完整 9 字段一致（曾漏 fMask/hwnd/lpParameters → 从第 2 字段起整体错位，
    // native 把 lpVerb 低 32 位读成 fMask → 全部 E_INVALIDARG，真机多轮踩坑后对照头文件修正）。
    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFO
    {
        public int cbSize;
        public int fMask;           // 0
        public IntPtr hwnd;         // owner 窗口
        public IntPtr lpVerb;       // MAKEINTRESOURCE 偏移（cmdId-idCmdFirst）
        public IntPtr lpParameters; // 文件夹动词用；文件动词留空
        public IntPtr lpDirectory;
        public int nShow;
        public int dwHotKey;        // DWORD(4B)
        public IntPtr hIcon;
    }

    // IShellFolder vtable（继承 IUnknown 3 方法后）：ParseDisplayName, EnumObjects, BindToObject,
    // BindToStorage, CompareIDs, CreateViewObject, GetAttributesOf, GetUIObjectOf（本类只调用它）。
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            IntPtr pchEaten, out IntPtr ppidl, IntPtr pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr penumList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
        // 关键：COM 接口方法的数组参数默认封送 SAFEARRAY，而 native 期望原生指针数组（LPArray）——
        // 不加 MarshalAs(LPArray) 会在 native 侧访问违例崩溃（0xC0000005，事件日志实锤）。
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl,
            [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid,
            IntPtr pvReserved, out IntPtr ppv);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPath(string pszPath);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IShellFolder ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    private const uint GCS_VERBW = 0x0005; // Unicode 动词名（ANSI 接口亦可请求）

    /// <summary>取菜单项动词名（如 "edit"/"openas"）；失败或空（偏移型 verb）返回 null。</summary>
    private static string? GetVerbString(IContextMenu cm, uint cmd)
    {
        const int cch = 256;
        var buf = Marshal.AllocHGlobal(cch * 2);
        try
        {
            if (cm.GetCommandString(cmd - 1, GCS_VERBW, 0, buf, (uint)cch) != 0) return null;
            var verb = Marshal.PtrToStringUni(buf);
            return string.IsNullOrWhiteSpace(verb) ? null : verb;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hmenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002, CMF_NORMAL = 0;

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr hmenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(IntPtr hmenu, uint uIDItem, System.Text.StringBuilder lpString, int nMaxCount, uint uFlag);
    [DllImport("user32.dll")]
    private static extern bool DeleteMenu(IntPtr hmenu, uint uPosition, uint uFlags);
    private const uint MF_BYPOSITION = 0x0400;

    /// <summary>按文字删除 HMENU 顶层项（不区分大小写包含匹配；从尾往头删避免索引位移）。</summary>
    private static void RemoveHiddenItems(IntPtr hmenu, IReadOnlyList<string> hiddenTexts)
    {
        for (var pos = GetMenuItemCount(hmenu) - 1; pos >= 0; pos--)
        {
            var sb = new System.Text.StringBuilder(256);
            GetMenuString(hmenu, (uint)pos, sb, sb.Capacity, MF_BYPOSITION);
            var text = sb.ToString();
            foreach (var h in hiddenTexts)
            {
                if (!string.IsNullOrWhiteSpace(h) &&
                    text.Contains(h.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    DeleteMenu(hmenu, (uint)pos, MF_BYPOSITION);
                    break;
                }
            }
        }
    }

    /// <summary>枚举系统菜单顶层项文字（不弹出；供设置页展示可过滤的菜单项列表）。</summary>
    public static List<string> EnumerateTopLevel(string filePath)
    {
        var result = new List<string>();
        var pidlFull = ILCreateFromPath(filePath);
        if (pidlFull == IntPtr.Zero) return result;
        try
        {
            var iidFolder = IID_IShellFolder;
            if (SHBindToParent(pidlFull, ref iidFolder, out var psf, out var pidlLast) != 0) return result;
            try
            {
                var iidCm = IID_IContextMenu;
                var apidl = new IntPtr[1] { pidlLast };
                if (psf.GetUIObjectOf(IntPtr.Zero, 1, apidl, ref iidCm, IntPtr.Zero, out var cmPtr) != 0) return result;
                var cm = (IContextMenu)Marshal.GetObjectForIUnknown(cmPtr);
                try
                {
                    var hmenu = CreatePopupMenu();
                    if (hmenu == IntPtr.Zero) return result;
                    try
                    {
                        if (cm.QueryContextMenu(hmenu, 0, 1, 0x7FFF, CMF_NORMAL) < 0) return result;
                        var count = GetMenuItemCount(hmenu);
                        for (var pos = 0; pos < count; pos++)
                        {
                            var sb = new System.Text.StringBuilder(256);
                            GetMenuString(hmenu, (uint)pos, sb, sb.Capacity, MF_BYPOSITION);
                            var text = sb.ToString().Trim();
                            if (text.Length > 0) result.Add(text);
                        }
                    }
                    finally { DestroyMenu(hmenu); }
                }
                finally { Marshal.ReleaseComObject(cm); }
            }
            finally { Marshal.ReleaseComObject(psf); }
        }
        finally { ILFree(pidlFull); }
        return result;
    }

    /// <summary>在鼠标位置弹出系统菜单并执行选中命令。hiddenTexts：按菜单文字过滤（不区分大小写包含匹配）。
    /// 返回是否成功弹出。</summary>
    public static bool Show(IntPtr ownerHwnd, string filePath, IReadOnlyList<string>? hiddenTexts = null)
    {
        var pidlFull = ILCreateFromPath(filePath);
        if (pidlFull == IntPtr.Zero) return false;
        try
        {
            var iidFolder = IID_IShellFolder;
            if (SHBindToParent(pidlFull, ref iidFolder, out var psf, out var pidlLast) != 0) return false;
            try
            {
                var iidCm = IID_IContextMenu;
                var apidl = new IntPtr[1] { pidlLast };
                if (psf.GetUIObjectOf(ownerHwnd, 1, apidl, ref iidCm, IntPtr.Zero, out var cmPtr) != 0 || cmPtr == IntPtr.Zero)
                    return false;
                var cm = (IContextMenu)Marshal.GetObjectForIUnknown(cmPtr);
                try
                {
                    var hmenu = CreatePopupMenu();
                    if (hmenu == IntPtr.Zero) return false;
                    try
                    {
                        if (cm.QueryContextMenu(hmenu, 0, 1, 0x7FFF, CMF_NORMAL) < 0) return false;
                        if (hiddenTexts is { Count: > 0 }) RemoveHiddenItems(hmenu, hiddenTexts);
                        int cmd;

                        // 菜单需要前台输入权限才能弹出。真机教训（2026-08-19）：
                        // ① 直接弹（NOACTIVATE）→ 菜单不显示；
                        // ② SetForegroundWindow(我们窗口) → 前台切到桌面 owned 窗口 = 系统 ShowDesktop 语义
                        //   → 全部窗口被最小化（像 Win+D）；
                        // → 正解：AttachThreadInput 共享前台线程的输入状态（原前台窗口保持前台），
                        //   弹完 detach。shell 上下文菜单经典做法。
                        var fg = GetForegroundWindow();
                        uint fgThread = GetWindowThreadProcessId(fg, out _);
                        uint curThread = GetCurrentThreadId();
                        bool attached = fgThread != 0 && fgThread != curThread &&
                                        AttachThreadInput(curThread, fgThread, true);
                        try
                        {
                            GetCursorPos(out var pt);
                            cmd = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, ownerHwnd, IntPtr.Zero);
                        }
                        finally
                        {
                            if (attached) AttachThreadInput(curThread, fgThread, false);
                        }
                        if (cmd == 0) return true; // 用户取消
                        var info = new CMINVOKECOMMANDINFO
                        {
                            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                            hwnd = ownerHwnd,
                            lpVerb = (IntPtr)(cmd - 1),
                            nShow = 1, // SW_SHOWNORMAL
                        };
                        var hr = cm.InvokeCommand(ref info);
                        if (hr != 0)
                        {
                            // Store 应用注入的菜单扩展（如 Win11 记事本「在记事本中编辑」）对程序化
                            // InvokeCommand 返回 E_INVALIDARG（缺站点上下文，已知兼容问题）→
                            // 降级：取动词名走 Process.Start(verb)（ShellExecute 路径，与系统双击同源）。
                            var verb = GetVerbString(cm, (uint)cmd);
                            if (verb is { Length: > 0 })
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath)
                                {
                                    UseShellExecute = true,
                                    Verb = verb,
                                });
                                return true;
                            }
                            throw new COMException($"InvokeCommand 失败 cmd={cmd} hr=0x{hr:X8}（且无动词名可降级）", hr);
                        }
                        return true;
                    }
                    finally { DestroyMenu(hmenu); }
                }
                finally { Marshal.ReleaseComObject(cm); }
            }
            finally { Marshal.ReleaseComObject(psf); }
        }
        finally { ILFree(pidlFull); }
    }
}
