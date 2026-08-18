using System.Runtime.InteropServices;

namespace DesktopManager.Player.Icons;

/// <summary>系统 shell 右键菜单（资源管理器同款：打开方式/第三方扩展/属性）。
/// SHParseDisplayName → IContextMenu → QueryContextMenu 生成 HMENU → TrackPopupMenuEx → InvokeCommand。
/// 调用线程须为窗口线程（弹出模态菜单）。</summary>
internal static class SystemContextMenu
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
        public IntPtr lpVerb;     // MAKEINTRESOURCE 偏移（cmdId-1）
        public IntPtr lpDirectory;
        public int nShow;
        public IntPtr dwHotKey;
        public IntPtr hIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IntPtr ppv);

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

    private static Guid IID_IShellItem => new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static Guid IID_IContextMenu => new("000214E4-0000-0000-C000-000000000046");

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint CMF_NORMAL = 0;

    /// <summary>在鼠标位置弹出系统菜单并执行选中命令。返回是否成功弹出。</summary>
    public static bool Show(IntPtr ownerHwnd, string filePath)
    {
        if (SHParseDisplayName(filePath, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
            return false;
        try
        {
            // 拿文件 IShellItem → BindToHandler(IContextMenu)
            var iidItem = IID_IShellItem;
            if (SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref iidItem, out var item) != 0)
                return false;
            try
            {
                var bh = (IShellItemBindToHandler)Marshal.GetObjectForIUnknown(item);
                var iidCm = IID_IContextMenu;
                bh.BindToHandler(IntPtr.Zero, ref iidCm, ref iidCm, out var cmPtr);
                if (cmPtr == IntPtr.Zero) return false;
                var cm = (IContextMenu)Marshal.GetObjectForIUnknown(cmPtr);
                try
                {
                    var hmenu = CreatePopupMenu();
                    if (hmenu == IntPtr.Zero) return false;
                    try
                    {
                        if (cm.QueryContextMenu(hmenu, 0, 1, 0x7FFF, CMF_NORMAL) < 0) return false;
                        GetCursorPos(out var pt);
                        var cmd = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                            pt.X, pt.Y, ownerHwnd, IntPtr.Zero);
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
            finally { Marshal.Release(item); }
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    [ComImport, Guid("BC8FAB30-492F-11D1-8E1D-00A0C92C9D5D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemBindToHandler
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    }
}
