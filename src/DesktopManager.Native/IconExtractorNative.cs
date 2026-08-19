using System.Runtime.InteropServices;

namespace DesktopManager.Native;

/// <summary>
/// SHGetFileInfo P/Invoke 封装：提取文件图标的 HICON 句柄。
/// 调用方（App 层 IconExtractor）负责把 HICON 转 BitmapSource 后立即调 Destroy 释放。
/// </summary>
public static class IconExtractorNative
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    // SHGetFileInfo 只有一个导出：pszPath 位传 pidl 时以 SHGFI_PIDL 标志区分（不存在
    // “SHGetFileInfoPidl” 导出——曾误命名导致 EntryPointNotFoundException 崩溃循环）。
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SHGetFileInfo")]
    private static extern IntPtr SHGetFileInfoByPidl(IntPtr pidl, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl,
        uint sfgaoIn, out uint psfgaoOut);

    private const uint SHGFI_PIDL = 0x000000008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // 48/64 档：SHGetImageList 取系统 imagelist（EXTRALARGE=48；JUMBO=256 由 WPF 缩到目标尺寸）
    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const int SHIL_SMALL = 1, SHIL_EXTRALARGE = 2, SHIL_JUMBO = 4;
    private const int ILD_TRANSPARENT = 1;

    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        // vtable 序（占位签名只需 slot 数正确；仅 GetIcon 会被调用）
        int Add(IntPtr a, IntPtr b, int c, out int d);
        int ReplaceIcon(int a, IntPtr b, out int c);
        int SetOverlayImage(int a, int b);
        int Replace(int a, IntPtr b, IntPtr c);
        int AddMasked(IntPtr a, int b, out int c);
        int Draw(IntPtr a);
        int Remove(int a);
        int GetIcon(int i, int flags, out IntPtr picon);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IImageList ppv);

    private static Guid IID_IImageList => new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    /// <summary>提取文件图标 HICON。size：16/32 直接 SHGFI；48=EXTRALARGE；≥64=JUMBO(256) 由显示端缩放。
    /// 返回 IntPtr.Zero 表示失败。</summary>
    public static IntPtr GetHIcon(string filePath, int size = 32)
    {
        // shell 虚拟对象（::{CLSID}，此电脑/回收站）：Win11 的 SHGetFileInfo 不认纯 CLSID 字符串
        // （hIcon=0，真机实测），必须 PIDL 方式（SHParseDisplayName + SHGFI_PIDL）。
        if (filePath.StartsWith("::", StringComparison.Ordinal))
        {
            if (SHParseDisplayName(filePath, IntPtr.Zero, out var pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
            {
                try
                {
                    var f = new SHFILEINFO();
                    SHGetFileInfoByPidl(pidl, 0, ref f, (uint)Marshal.SizeOf<SHFILEINFO>(),
                        SHGFI_ICON | SHGFI_PIDL | (size <= 16 ? SHGFI_SMALLICON : SHGFI_LARGEICON));
                    if (f.hIcon != IntPtr.Zero) return f.hIcon;
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
            return IntPtr.Zero;
        }
        if (size > 32)
        {
            var fi = new SHFILEINFO();
            SHGetFileInfo(filePath, 0, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX);
            var shil = size <= 48 ? SHIL_EXTRALARGE : SHIL_JUMBO;
            var iid = IID_IImageList;
            if (SHGetImageList(shil, ref iid, out var list) == 0)
            {
                try
                {
                    if (list.GetIcon(fi.iIcon, ILD_TRANSPARENT, out var hicon) == 0)
                        return hicon;
                }
                finally { Marshal.ReleaseComObject(list); }
            }
            // 失败降级 32
        }
        var f2 = new SHFILEINFO();
        uint flags2 = SHGFI_ICON | (size <= 16 ? SHGFI_SMALLICON : SHGFI_LARGEICON);
        SHGetFileInfo(filePath, 0, ref f2, (uint)Marshal.SizeOf<SHFILEINFO>(), flags2);
        return f2.hIcon;
    }

    /// <summary>调用方取完 BitmapSource 后应释放 HICON。</summary>
    public static void Destroy(IntPtr hIcon)
    {
        if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
    }
}
