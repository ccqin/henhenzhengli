using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopManager.Core.Models;
using DesktopManager.Native;
using Serilog;

namespace DesktopManager.App.Windows;

/// <summary>M4/M5 壁纸播放窗口（每显示器一个）。
/// 单屏模式：内容铺满本窗。组模式（<paramref name="canvas"/> 非 null）：内容按 cover 缩放到虚拟画布尺寸，
/// 负偏移放置，窗口 ClipToBounds 裁出本屏区域——跨屏拼接接缝天然对齐（图/GIF/视频统一路径）。
/// 整屏高 -2px（缝藏任务栏后）：破 shell「全屏 app」检测（M4 真机教训）。
/// Z-order/可见性 Win32 兜底见 SyncNativeState；host 看门狗重锚底序。</summary>
public partial class WallpaperWindow : Window
{
    // Win32 常量和 P/Invoke
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int WM_SHOWWINDOW = 0x0018;

    
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly string _monitorId;
    private IntPtr _hwnd;
    private (int X, int Y, int W, int H) _monRect;

    // 内容元素（代码创建，Canvas 偏移放置）
    private readonly Image _image = new() { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
    private readonly MediaElement _video = new()
    {
        LoadedBehavior = MediaState.Manual,
        UnloadedBehavior = MediaState.Manual,
        Volume = 0,
        IsHitTestVisible = false,
        Stretch = Stretch.Fill,
        Visibility = Visibility.Collapsed
    };

    // GIF 帧动画（MVP 固定 10fps 钳制；逐帧 delay 解析 backlog）
    private GifBitmapDecoder? _gif;
    private DispatcherTimer? _gifTimer;
    private int _gifFrame;

    private bool _paused;
    private bool _hasPlayback;

    // 放置规格（RepositionTo / 内容加载后重算）
    private WallpaperKind _kind = WallpaperKind.Image;
    private IntRect? _canvas;           // 组模式虚拟画布；null = 单屏模式
    private double _natW, _natH;        // 图像自然尺寸（cover 计算用）

    /// <summary>本窗口归属屏持久 ID。</summary>
    public string MonitorId => _monitorId;

    /// <summary>当前有可暂停播放且未暂停（Governor 诊断用）。</summary>
    public bool IsPlaying => _hasPlayback && !_paused;

    /// <summary>M5-T4：视频当前位置（组内漂移校正用）。</summary>
    public TimeSpan VideoPosition
    {
        get => _video.Position;
        set => _video.Position = value;
    }

    /// <summary>M5-T4：是否视频模式（校正只针对视频）。</summary>
    public bool IsVideo => _kind == WallpaperKind.Video && _hasPlayback;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    public WallpaperWindow(MonitorInfo monitor)
    {
        _monitorId = monitor.PersistentId;
        _monRect = (monitor.X, monitor.Y, monitor.Width, monitor.Height);
        InitializeComponent();
        RootCanvas.Children.Add(_image);
        RootCanvas.Children.Add(_video);
        // 整屏但底部留 2px 缝（M4 真机教训：破 shell 全屏检测）。
        Left = monitor.X; Top = monitor.Y; Width = monitor.Width; Height = monitor.Height - 2;
                SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            
            // WS_EX_TOOLWINDOW：不在任务栏显示
            int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
            exStyle |= WS_EX_TOOLWINDOW;
            SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
            
            WindowInterop.MakeClickThrough(_hwnd);
            
            // 在 MakeClickThrough 后补 TOOLWINDOW（MakeClickThrough 覆盖了 ex style）
            var ex = WindowInterop.GetExtendedStyle(_hwnd);
            WindowInterop.SetExtendedStyle(_hwnd, ex | 0x00000080);
        };
        _video.MediaEnded += (_, _) =>
        {
            if (_paused) return;
            try { _video.Position = TimeSpan.Zero; _video.Play(); }
            catch (Exception ex) { Log.Warning(ex, "视频循环重播失败：{Mon}", _monitorId); }
        };
        _video.MediaFailed += (_, e) =>
        {
            Log.Warning("视频播放失败（编码不支持？），回退系统壁纸：{Mon} {Err}", _monitorId, e.ErrorException?.Message);
            StopPlayback();
            Visibility = Visibility.Hidden;
            SyncNativeState();
        };
        _video.MediaOpened += (_, _) =>
        {
            // 视频自然尺寸已知 → cover 重算（组模式接缝对齐）。
            if (_video.NaturalVideoWidth > 0)
            {
                _natW = _video.NaturalVideoWidth;
                _natH = _video.NaturalVideoHeight;
                ApplyPlacement();
            }
        };
        Visibility = Visibility.Hidden;
    }

    /// <summary>M3-T6 同款：拓扑变化重定位。</summary>
    public void RepositionTo(MonitorInfo monitor)
    {
        _monRect = (monitor.X, monitor.Y, monitor.Width, monitor.Height);
        Left = monitor.X; Top = monitor.Y;
        Width = monitor.Width; Height = monitor.Height - 2;
        ApplyPlacement();
    }

    /// <summary>应用壁纸。canvas=null 单屏铺满；非 null 组模式跨屏裁剪。</summary>
    public void SetWallpaper(WallpaperConfig? config, IntRect? canvas = null)
    {
        StopPlayback();
        _canvas = canvas;
        if (config is null || string.IsNullOrWhiteSpace(config.Path) || !File.Exists(config.Path))
        {
            _image.Source = null;
            Visibility = Visibility.Hidden;
            SyncNativeState();
            return;
        }

        _kind = WallpaperConfig.DetectKind(config.Path);
        try
        {
            switch (_kind)
            {
                case WallpaperKind.Video:
                    _natW = _natH = 0; // MediaOpened 后 cover 重算
                    _image.Visibility = Visibility.Collapsed;
                    _video.Visibility = Visibility.Visible;
                    _video.Source = new Uri(config.Path);
                    _video.Play();
                    _hasPlayback = true;
                    break;
                case WallpaperKind.Gif:
                    if (!ApplyGif(config.Path)) ApplyImage(config.Path);
                    break;
                default:
                    ApplyImage(config.Path);
                    break;
            }
            Visibility = Visibility.Visible;
            SyncNativeState();
            ApplyPlacement();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SyncNativeState();
                ApplyPlacement(); // 首帧后 ActualWidth 可用
            }), DispatcherPriority.Loaded);
            Log.Information("壁纸已应用: {Path} kind={Kind} group={Group}", config.Path, _kind, canvas is not null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "壁纸加载失败，回退系统壁纸：{Path}", config.Path);
            StopPlayback();
            _image.Source = null;
            Visibility = Visibility.Hidden;
            SyncNativeState();
        }
    }

    private void ApplyImage(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
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

    /// <summary>内容放置：单屏铺满窗口；组模式按 cover 缩放到虚拟画布 + 负偏移裁出本屏。</summary>
    private void ApplyPlacement()
    {
        double winW = ActualWidth, winH = ActualHeight;
        if (winW <= 0 || winH <= 0) return;

        if (_canvas is null)
        {
            Place(_image, 0, 0, winW, winH);
            Place(_video, 0, 0, winW, winH);
            return;
        }

        var canvas = _canvas;
        var mon = new IntRect(_monRect.X, _monRect.Y, _monRect.X + _monRect.W, _monRect.Y + _monRect.H);
        double offX = -(mon.Left - canvas.Left);
        double offY = -(mon.Top - canvas.Top);

        if (_kind == WallpaperKind.Video && _natW <= 0)
        {
            // 视频自然尺寸未知（MediaOpened 前）：先 Fill 画布，opened 后 cover 重算。
            Place(_video, offX, offY, canvas.Width, canvas.Height);
            return;
        }

        if (_natW > 0 && _natH > 0)
        {
            // cover：等比缩放铺满画布，超出居中裁掉（与 CrossScreenLayout.CropRect 同语义）
            double s = Math.Max(canvas.Width / _natW, canvas.Height / _natH);
            double cw = _natW * s, ch = _natH * s;
            double cx = offX + (canvas.Width - cw) / 2;
            double cy = offY + (canvas.Height - ch) / 2;
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

    /// <summary>M4-T4：暂停（幂等）。</summary>
    public void Pause()
    {
        if (_paused || !_hasPlayback) return;
        _paused = true;
        try { _video.Pause(); } catch { /* 无视频 no-op */ }
        _gifTimer?.Stop();
        Log.Information("壁纸暂停：{Mon}", _monitorId);
    }

    /// <summary>M4-T4：恢复（幂等）。</summary>
    public void Resume()
    {
        if (!_paused || !_hasPlayback) return;
        _paused = false;
        try { _video.Play(); } catch { /* 无视频 no-op */ }
        _gifTimer?.Start();
        Log.Information("壁纸恢复：{Mon}", _monitorId);
    }

    private void StopPlayback()
    {
        _gifTimer?.Stop();
        _gifTimer = null;
        _gif = null;
        _gifFrame = 0;
        try
        {
            _video.Stop();
            _video.Source = null;
        }
        catch { /* 未加载 no-op */ }
        _video.Visibility = Visibility.Collapsed;
        _image.Visibility = Visibility.Collapsed;
        _hasPlayback = false;
        _paused = false;
        _natW = _natH = 0;
    }

    /// <summary>可见性 Win32 兜底（M4 真机：WPF Visibility 可能不落 WS_VISIBLE）。</summary>
    private void SyncNativeState()
    {
        if (_hwnd == IntPtr.Zero) return;
        ShowWindow(_hwnd, Visibility == Visibility.Visible ? SW_SHOWNOACTIVATE : SW_HIDE);
    }
}
