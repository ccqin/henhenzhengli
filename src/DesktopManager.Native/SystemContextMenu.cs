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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFO
    {
        public int cbSize;
        public IntPtr lpVerb;     // 命令偏移（cmdId-1）
        public IntPtr lpDirectory;
        public int nShow;
        public IntPtr dwHotKey;
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
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [In] IntPtr[] apidl, ref Guid riid,
            IntPtr pvReserved, out IntPtr ppv);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPath(string pszPath);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IShellFolder ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hmenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

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

                        // NOACTIVATE 窗口 TrackPopupMenu 弹不出（菜单需前台窗口接收消息，真机踩坑）
                        // → 弹出前临时前台化，结束恢复 NOACTIVATE。
                        var prevEx = WindowInterop.EnableActivation(ownerHwnd);
                        GetCursorPos(out var pt);
                        var cmd = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, ownerHwnd, IntPtr.Zero);
                        WindowInterop.RestoreNonInteractive(ownerHwnd, prevEx);
                        if (cmd == 0) return true; // 用户取消
                        var info = new CMINVOKECOMMANDINFO
                        {
                            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                            lpVerb = (IntPtr)(cmd - 1),
                            nShow = 1, // SW_SHOWNORMAL
                        };
                        cm.InvokeCommand(ref info);
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
