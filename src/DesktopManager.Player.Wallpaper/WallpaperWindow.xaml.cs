using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopManager.Core.Models;
using DesktopManager.Ipc;

namespace DesktopManager.Player.Wallpaper;

/// <summary>壁纸子进程渲染窗口（逻辑移植自 App.WallpaperWindow，M6）。
/// 单屏模式：内容铺满本窗。组模式（CanvasW&gt;0）：内容 cover 缩放到虚拟画布 + 负偏移，
/// 窗口 ClipToBounds 裁出本屏区域——跨屏拼接接缝天然对齐。
/// SetParent 到 WorkerW 由主进程负责（本类不做 Win32 挂载）；初始 Hidden 等主进程 Show。</summary>
public partial class WallpaperWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private IntPtr _hwnd;

    private readonly Image _image = new() { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
    private readonly MediaElement _video = new()
    {
        LoadedBehavior = MediaState.Manual,
        UnloadedBehavior = MediaState.Manual,
        Volume = 0,
        IsHitTestVisible = false,
        Stretch = Stretch.Fill,
        Visibility = Visibility.Collapsed,
        ScrubbingEnabled = false, // GPU 优化③：暂停/跳转不渲染帧
    };

    /// <summary>视频位置上报（App 转 IPC，主进程组内对齐用）。</summary>
    public event Action<double>? VideoPositionChanged;

    /// <summary>视频分辨率超过屏幕（w,h,screenW,screenH）——GPU 浪费提示。</summary>
    public event Action<int, int, int, int>? VideoOversized;

    private DispatcherTimer? _videoReportTimer;
    private GifBitmapDecoder? _gif;
    private DispatcherTimer? _gifTimer;
    private int _gifFrame;

    private bool _paused;
    private bool _hasPlayback;
    private WallpaperKind _kind = WallpaperKind.Image;
    private int _canvasW, _canvasH;     // 组模式虚拟画布；0 = 单屏模式
    private int _cropX, _cropY;         // 本屏在虚拟画布中的偏移（负偏移放置用）
    private double _natW, _natH;
    private bool _pendingShow;          // 主进程 Show 指令已到但内容未就绪

    public WallpaperWindow(int x, int y, int w, int h)
    {
        InitializeComponent();
        RootCanvas.Children.Add(_image);
        RootCanvas.Children.Add(_video);
        Left = x; Top = y; Width = w; Height = h;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        _video.MediaEnded += (_, _) =>
        {
            if (_paused) return;
            try { _video.Position = TimeSpan.Zero; _video.Play(); } catch { /* 循环重播失败 */ }
        };
        _video.MediaOpened += (_, _) =>
        {
            if (_video.NaturalVideoWidth > 0)
            {
                _natW = _video.NaturalVideoWidth;
                _natH = _video.NaturalVideoHeight;
                ApplyPlacement();
                // GPU 优化④：视频分辨率高于屏幕 → 解码浪费（4K 视频在 1080p 屏全解码再缩小）
                if (_natW > SystemParameters.PrimaryScreenWidth || _natH > SystemParameters.PrimaryScreenHeight)
                {
                    VideoOversized?.Invoke(_natW, _natH,
                        (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);
                }
            }
        };
        Visibility = Visibility.Hidden;
    }

    /// <summary>主进程 Show 指令：SetParent 完成后才显示（此前保持 Hidden，防 DWM 拒绝合成）。</summary>
    public void ShowLayer()
    {
        _pendingShow = true;
        if (_image.Source is not null || _video.Source is not null)
            Visibility = Visibility.Visible;
    }

    public void RepositionTo(int x, int y, int w, int h)
    {
        Left = x; Top = y; Width = w; Height = h;
        ApplyPlacement();
    }

    /// <summary>IPC SetWallpaper：path + kind + 可选跨屏裁剪。</summary>
    public void ApplyWallpaper(SetWallpaper w)
    {
        StopPlayback();
        _canvasW = w.CanvasW;
        _canvasH = w.CanvasH;
        _cropX = w.CropX ?? 0;
        _cropY = w.CropY ?? 0;

        if (string.IsNullOrWhiteSpace(w.Path) || !File.Exists(w.Path))
        {
            _image.Source = null;
            Visibility = Visibility.Hidden;
            return;
        }

        _kind = w.Kind switch
        {
            "video" => WallpaperKind.Video,
            "gif" => WallpaperKind.Gif,
            _ => WallpaperConfig.DetectKind(w.Path),
        };
        try
        {
            switch (_kind)
            {
                case WallpaperKind.Video:
                    _natW = _natH = 0;
                    _gifTimer?.Stop(); // GPU 优化②：视频播放时停 GIF 定时器（防空转）
                    _image.Visibility = Visibility.Collapsed;
                    _video.Visibility = Visibility.Visible;
                    _video.Source = new Uri(GetVideoCachedPath(w.Path));
                    _video.Play();
                    _hasPlayback = true;
                    StartVideoReport();
                    break;
                case WallpaperKind.Gif:
                    if (!ApplyGif(w.Path)) ApplyImage(w.Path);
                    break;
                default:
                    ApplyImage(w.Path);
                    break;
            }
            // 无壁纸时 Hidden；有内容但主进程尚未发 Show 也保持 Hidden。
            // 只在主进程已发 Show 且有内容时显示（SetParent 前显示 = DWM 黑屏，真机教训）。
            Visibility = _pendingShow && (_image.Source is not null || _video.Source is not null)
                ? Visibility.Visible
                : Visibility.Hidden;
            ApplyPlacement();
            Dispatcher.BeginInvoke(new Action(ApplyPlacement), DispatcherPriority.Loaded);
        }
        catch
        {
            StopPlayback();
            _image.Source = null;
            Visibility = Visibility.Hidden;
        }
    }

    private void ApplyImage(string path)
    {
        BitmapImage bmp = new();
        try
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
        }
        catch (Exception)
        {
            // 真机：非常规 JPEG（AI 生成/部分下载器产物）WPF/WIC 解码报"元数据头损坏"但 GDI+ 可读
            // → GDI+ 解码转 PNG 流兜底，壁纸仍能显示。
            using var sd = System.Drawing.Image.FromFile(path);
            using var ms = new MemoryStream();
            sd.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
        }
        bmp.Freeze();
        _natW = bmp.PixelWidth;
        _natH = bmp.PixelHeight;
        _image.Source = bmp;
        _image.Visibility = Visibility.Visible;
        _video.Visibility = Visibility.Collapsed;
        _hasPlayback = false;
    }

    private bool ApplyGif(string path)
    {
        _gif = new GifBitmapDecoder(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (_gif.Frames.Count <= 1) { _gif = null; return false; }
        _natW = _gif.Frames[0].PixelWidth;
        _natH = _gif.Frames[0].PixelHeight;
        _gifFrame = 0;
        _image.Source = _gif.Frames[0];
        _image.Visibility = Visibility.Visible;
        _video.Visibility = Visibility.Collapsed;
        _hasPlayback = true;
        _gifTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _gifTimer.Tick += (_, _) =>
        {
            if (_gif is null) return;
            _gifFrame = (_gifFrame + 1) % _gif.Frames.Count;
            _image.Source = _gif.Frames[_gifFrame];
        };
        _gifTimer.Start();
        return true;
    }

    private void ApplyPlacement()
    {
        double winW = ActualWidth, winH = ActualHeight;
        if (winW <= 0 || winH <= 0) return;

        if (_canvasW <= 0 || _canvasH <= 0)
        {
            Place(_image, 0, 0, winW, winH);
            Place(_video, 0, 0, winW, winH);
            return;
        }

        double offX = -_cropX;
        double offY = -_cropY;

        if (_kind == WallpaperKind.Video && _natW <= 0)
        {
            Place(_video, offX, offY, _canvasW, _canvasH);
            return;
        }

        if (_natW > 0 && _natH > 0)
        {
            double s = Math.Max(_canvasW / _natW, _canvasH / _natH);
            double cw = _natW * s, ch = _natH * s;
            double cx = offX + (_canvasW - cw) / 2;
            double cy = offY + (_canvasH - ch) / 2;
            Place(_image, cx, cy, cw, ch);
            Place(_video, cx, cy, cw, ch);
        }
    }

    private static void Place(FrameworkElement el, double l, double t, double w, double h)
    {
        Canvas.SetLeft(el, l);
        Canvas.SetTop(el, t);
        el.Width = w;
        el.Height = h;
    }

    public void Pause()
    {
        if (_paused || !_hasPlayback) return;
        _paused = true;
        try { _video.Pause(); } catch { }
        _gifTimer?.Stop();
    }

    public void Resume()
    {
        if (!_paused || !_hasPlayback) return;
        _paused = false;
        try { _video.Play(); } catch { }
        _gifTimer?.Start();
    }

    /// <summary>视频内存缓存路径：首次播放复制到 %TEMP%（FILE_ATTRIBUTE_TEMPORARY → OS 文件缓存
    /// 优先进 RAM，循环播放不再逐块读原文件——解决频繁磁盘 I/O）。原文件变更时重拷（LastWriteTime 比对）。</summary>
    private static readonly Dictionary<string, string> _videoCache = new(StringComparer.OrdinalIgnoreCase);

    private static string GetVideoCachedPath(string originalPath)
    {
        try
        {
            if (!_videoCache.TryGetValue(originalPath, out var cached) || !File.Exists(cached))
            {
                var srcTime = File.GetLastWriteTimeUtc(originalPath);
                // 保留原扩展名（MediaElement 按后缀识别媒体格式，.cache 无法播放）
                cached = Path.Combine(Path.GetTempPath(),
                    "DM_video_" + Math.Abs(originalPath.GetHashCode()) + Path.GetExtension(originalPath));
                var needCopy = !File.Exists(cached) ||
                               File.GetLastWriteTimeUtc(cached) != srcTime;
                if (needCopy)
                {
                    File.Copy(originalPath, cached, overwrite: true);
                    // TEMPORARY：提示 OS 数据是临时的，尽量驻留内存（lazy write）
                    File.SetAttributes(cached, File.GetAttributes(cached) | FileAttributes.Temporary);
                    File.SetLastWriteTimeUtc(cached, srcTime);
                }
                _videoCache[originalPath] = cached;
            }
            return cached;
        }
        catch
        {
            return originalPath; // 复制失败退回原路径（正常流式播放）
        }
    }

    /// <summary>视频跳转（组内对齐）。</summary>
    public void Seek(double positionMs)
    {
        try { _video.Position = TimeSpan.FromMilliseconds(positionMs); } catch { }
    }

    private void StartVideoReport()
    {
        _videoReportTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _videoReportTimer.Tick += (_, _) =>
        {
            if (_kind == WallpaperKind.Video && _hasPlayback && !_paused)
                VideoPositionChanged?.Invoke(_video.Position.TotalMilliseconds);
        };
        _videoReportTimer.Start();
    }

    private void StopPlayback()
    {
        _videoReportTimer?.Stop();
        _gifTimer?.Stop();
        _gifTimer = null;
        _gif = null;
        _gifFrame = 0;
        try
        {
            _video.Stop();
            _video.Source = null;
        }
        catch { }
        _video.Visibility = Visibility.Collapsed;
        _image.Visibility = Visibility.Collapsed;
        _hasPlayback = false;
        _paused = false;
        _natW = _natH = 0;
    }
}
