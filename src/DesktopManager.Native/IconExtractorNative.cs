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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>提取文件图标 HICON。size <= 16 走小图标，否则大图标。返回 IntPtr.Zero 表示失败。</summary>
    public static IntPtr GetHIcon(string filePath, int size = 32)
    {
        var fi = new SHFILEINFO();
        uint flags = SHGFI_ICON | (size <= 16 ? SHGFI_SMALLICON : SHGFI_LARGEICON);
        SHGetFileInfo(filePath, 0, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        return fi.hIcon;
    }

    /// <summary>调用方取完 BitmapSource 后应释放 HICON。</summary>
    public static void Destroy(IntPtr hIcon)
    {
        if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
    }
}
