using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DesktopManager.Native;

namespace DesktopManager.Player.Icons;

/// <summary>
/// 把 IconExtractorNative 返回的 HICON 转为 WPF BitmapSource，带路径->BitmapSource 字典缓存。
/// Freeze 后 BitmapSource 跨线程可用；缓存访问用 lock 保护。
/// </summary>
public sealed class IconExtractor
{
    private readonly Dictionary<string, BitmapSource> _cache = new(StringComparer.OrdinalIgnoreCase);
    // 当前提取尺寸档（外观设置下发；切换时清缓存重新提取）。
    public int Size { get; set; } = 48;

    public BitmapSource? GetIcon(string filePath)
    {
        var key = filePath + "|" + Size;
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var hit)) return hit;
        }
        IntPtr hicon = IconExtractorNative.GetHIcon(filePath, Size);
        if (hicon == IntPtr.Zero) return null;
        try
        {
            // 注意：CreateBitmapSourceFromHIcon 标准签名仅 3 参数（无 palette）；
            // brief 原文写的 4 参数（含 IntPtr.Zero palette）实为 CreateBitmapSourceFromHBitmap 的签名，
            // 此处按微软官方文档修正为 3 参数。
            var bmp = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze(); // 跨线程可用
            lock (_cache) { _cache[key] = bmp; }
            return bmp;
        }
        finally { IconExtractorNative.Destroy(hicon); }
    }
}
