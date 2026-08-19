using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopManager.App.Services;

/// <summary>图片加载器：WPF/WIC 失败（非常规 JPEG，如部分 AI 生图产物）时 GDI+ 解码兜底。
/// 壁纸渲染（Player.Wallpaper）与设置窗口预览共用同一容错语义。</summary>
public static class ImageLoader
{
    /// <summary>加载为可渲染位图；失败返回 null。</summary>
    public static ImageSource? Load(string path, int decodePixelWidth = 0)
    {
        if (!File.Exists(path)) return null;
        BitmapImage? bmp = null;
        try
        {
            bmp = new BitmapImage();
            bmp.BeginInit();
            if (decodePixelWidth > 0) bmp.DecodePixelWidth = decodePixelWidth;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
        }
        catch (Exception)
        {
            bmp = TryGdiPlus(path, decodePixelWidth); // WIC 拒绝的非常规 JPEG
        }
        bmp?.Freeze();
        return bmp;
    }

    private static BitmapImage? TryGdiPlus(string path, int decodeWidth)
    {
        try
        {
            using var sd = System.Drawing.Image.FromFile(path);
            using var ms = new MemoryStream();
            sd.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            var fb = new BitmapImage();
            fb.BeginInit();
            if (decodeWidth > 0) fb.DecodePixelWidth = decodeWidth;
            fb.CacheOption = BitmapCacheOption.OnLoad;
            fb.StreamSource = ms;
            fb.EndInit();
            return fb;
        }
        catch { return null; }
    }
}
