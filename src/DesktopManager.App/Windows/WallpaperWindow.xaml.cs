using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DesktopManager.Core.Models;
using DesktopManager.Native;
using Serilog;

namespace DesktopManager.App.Windows;

/// <summary>M4 壁纸播放窗口（每显示器一个，位于本屏图标层正下方）。
/// 全屏整屏 rect（含任务栏区）、点击穿透、NOACTIVATE。
/// **不开 AllowsTransparency**：MediaElement 在透明窗内不渲染（WPF 硬约束）；无壁纸用 Visibility=Hidden
/// 让系统壁纸透出。Z-order 由 host 用 BottomPair 编排（图标层置底 + 本窗插其下）。
/// 内容三态：静态图（Image）/ 视频（MediaElement 无声循环）/ GIF（帧动画，固定 10fps 钳制）。</summary>
public partial class WallpaperWindow : Window
{
    private readonly string _monitorId;
    private IntPtr _hwnd;

    // GIF 帧动画：DispatcherTimer 切帧（MVP 固定 10fps；逐帧 delay 解析记 backlog）。
    private GifBitmapDecoder? _gif;
    private System.Windows.Threading.DispatcherTimer? _gifTimer;
    private int _gifFrame;

    // 播放状态（Governor 幂等暂停/恢复用）。
    private bool _paused;
    private bool _hasPlayback; // 当前壁纸是视频/GIF（有可暂停的播放）

    /// <summary>本窗口归属屏持久 ID（host 按 MonitorId 分发壁纸配置）。</summary>
    public string MonitorId => _monitorId;

    /// <summary>M4：多屏宿主（host 创建时注入），Z-order 重锚用。</summary>
    public MultiMonitorHost? Host { get; set; }

    /// <summary>当前有可暂停的播放内容且未在暂停中（Governor 决策输入之一，仅日志/诊断用）。</summary>
    public bool IsPlaying => _hasPlayback && !_paused;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    public WallpaperWindow(MonitorInfo monitor)
    {
        _monitorId = monitor.PersistentId;
        InitializeComponent();
        // 整屏但底部留 2px 缝（藏在任务栏后，视觉不可见）：shell 的「全屏 app」检测要求精确覆盖
        // 整显示器（真机：GIF 连续渲染 + 全覆盖触发检测 → 任务栏被剥 topmost + 壁纸窗顶高盖任务栏）。
        // 破覆盖即破检测；任务栏 48px 高，2px 缝不可见。
        Left = monitor.X; Top = monitor.Y; Width = monitor.Width; Height = monitor.Height - 2;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowInterop.MakeClickThrough(_hwnd); // 置底 + 点击穿透 + NOACTIVATE
        };
        WallpaperVideo.MediaEnded += (_, _) =>
        {
            // 无声循环：播完回起点重播。暂停态不自动重播。
            if (_paused) return;
            try { WallpaperVideo.Position = TimeSpan.Zero; WallpaperVideo.Play(); }
            catch (Exception ex) { Log.Warning(ex, "视频循环重播失败：{Mon}", _monitorId); }
        };
        WallpaperVideo.MediaFailed += (_, e) =>
        {
            // 编码不支持（HEVC 等）：不崩，回退无壁纸（日志可诊断）。
            Log.Warning("视频播放失败（编码不支持？），回退系统壁纸：{Mon} {Err}", _monitorId, e.ErrorException?.Message);
            StopPlayback();
            Visibility = Visibility.Hidden;
            SyncNativeState();
        };
        Visibility = Visibility.Hidden; // 无壁纸默认：不遮系统壁纸
    }

    /// <summary>M3-T6 同款：分辨率/排列变化后重定位到新整屏 rect。</summary>
    public void RepositionTo(MonitorInfo monitor)
    {
        Left = monitor.X; Top = monitor.Y;
        Width = monitor.Width; Height = monitor.Height - 2; // 同构造：2px 缝破全屏检测
    }

    /// <summary>应用壁纸配置。null/空路径/文件不存在 → 隐藏（系统壁纸透出）。
    /// Kind 以扩展名实际校正（防用户改扩展名）：.gif→Gif；视频扩展名→Video；其余→Image。</summary>
    public void SetWallpaper(WallpaperConfig? config)
    {
        StopPlayback();
        if (config is null || string.IsNullOrWhiteSpace(config.Path) || !File.Exists(config.Path))
        {
            WallpaperImage.Source = null;
            Visibility = Visibility.Hidden;
            SyncNativeState();
            return;
        }

        var kind = DetectKind(config.Path);
        try
        {
            switch (kind)
            {
                case WallpaperKind.Video:
                    ApplyVideo(config.Path);
                    break;
                case WallpaperKind.Gif:
                    if (!ApplyGif(config.Path)) ApplyImage(config.Path); // 单帧 GIF 退化为静态图
                    break;
                default:
                    ApplyImage(config.Path);
                    break;
            }
            Visibility = Visibility.Visible;
            SyncNativeState();
            // WPF 首帧布局可能再隐藏（真机：同步 ShowWindow 后仍 vis=False）→ 延迟一帧强制对齐。
            Dispatcher.BeginInvoke(new Action(SyncNativeState), System.Windows.Threading.DispatcherPriority.Loaded);
            Log.Information("壁纸已应用: {Path} kind={Kind}", config.Path, kind);
        }
        catch (Exception ex)
        {
            // 文件损坏/格式不支持：不崩，回退无壁纸（日志可诊断）。
            Log.Warning(ex, "壁纸加载失败，回退系统壁纸：{Path}", config.Path);
            StopPlayback();
            WallpaperImage.Source = null;
            Visibility = Visibility.Hidden;
            SyncNativeState();
        }
    }

    /// <summary>扩展名校正 Kind（config.Kind 只做提示）。</summary>
    private static WallpaperKind DetectKind(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".gif" => WallpaperKind.Gif,
            ".mp4" or ".wmv" or ".avi" or ".m4v" => WallpaperKind.Video,
            _ => WallpaperKind.Image
        };
    }

    private void ApplyImage(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad; // 读完即释放文件句柄（不锁用户壁纸文件）
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze(); // 跨线程安全 + 省内存
        WallpaperImage.Source = bmp;
        WallpaperVideo.Visibility = Visibility.Collapsed;
        _hasPlayback = false;
    }

    private void ApplyVideo(string path)
    {
        WallpaperImage.Source = null;
        WallpaperVideo.Visibility = Visibility.Visible;
        WallpaperVideo.Source = new Uri(path);
        WallpaperVideo.Play();
        _hasPlayback = true;
    }

    /// <summary>GIF 帧动画。返回 false = 单帧（调用方退化为静态图）。</summary>
    private bool ApplyGif(string path)
    {
        _gif = new GifBitmapDecoder(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (_gif.Frames.Count <= 1) { _gif = null; return false; }

        _gifFrame = 0;
        WallpaperImage.Source = _gif.Frames[0];
        WallpaperVideo.Visibility = Visibility.Collapsed;
        _hasPlayback = true;
        _gifTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100) // 固定 10fps 钳制（逐帧 delay 解析记 backlog）
        };
        _gifTimer.Tick += (_, _) =>
        {
            if (_gif is null) return;
            _gifFrame = (_gifFrame + 1) % _gif.Frames.Count;
            WallpaperImage.Source = _gif.Frames[_gifFrame];
        };
        _gifTimer.Start();
        return true;
    }

    /// <summary>M4-T4：Governor 暂停（全屏应用/电池/锁屏）。幂等。</summary>
    public void Pause()
    {
        if (_paused || !_hasPlayback) return;
        _paused = true;
        try { WallpaperVideo.Pause(); } catch { /* 无视频内容时 no-op */ }
        _gifTimer?.Stop();
        Log.Information("壁纸暂停：{Mon}", _monitorId);
    }

    /// <summary>M4-T4：Governor 恢复。幂等。</summary>
    public void Resume()
    {
        if (!_paused || !_hasPlayback) return;
        _paused = false;
        try { WallpaperVideo.Play(); } catch { /* 无视频内容时 no-op */ }
        _gifTimer?.Start();
        Log.Information("壁纸恢复：{Mon}", _monitorId);
    }

    /// <summary>停掉一切播放（换壁纸/隐藏前调）。</summary>
    private void StopPlayback()
    {
        _gifTimer?.Stop();
        _gifTimer = null;
        _gif = null;
        _gifFrame = 0;
        try
        {
            WallpaperVideo.Stop();
            WallpaperVideo.Source = null;
        }
        catch { /* 未加载时 no-op */ }
        WallpaperVideo.Visibility = Visibility.Collapsed;
        _hasPlayback = false;
        _paused = false;
    }

    /// <summary>真机兜底：WPF Visibility 在创建时序下可能不落 WS_VISIBLE（日志 Visible 但 IsWindowVisible=false）→ Win32 强制对齐。</summary>
    private void SyncNativeState()
    {
        // 桌面层子窗只管可见性（Z-order 由桌面层天然保证；样式在 AttachToDesktopLayer 一次到位）。
        if (_hwnd == IntPtr.Zero) return;
        ShowWindow(_hwnd, Visibility == Visibility.Visible ? SW_SHOWNOACTIVATE : SW_HIDE);
    }
}
