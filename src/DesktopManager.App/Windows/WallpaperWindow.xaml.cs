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
/// 让系统壁纸透出。Z-order 由 host 用 <see cref="WindowInterop.PlaceBelow"/> 精确插到本屏图标层之下
/// （不靠创建顺序赌）。</summary>
public partial class WallpaperWindow : Window
{
    private readonly string _monitorId;
    private IntPtr _hwnd;

    /// <summary>本窗口归属屏持久 ID（host 按 MonitorId 分发壁纸配置）。</summary>
    public string MonitorId => _monitorId;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    public WallpaperWindow(MonitorInfo monitor)
    {
        _monitorId = monitor.PersistentId;
        InitializeComponent();
        // 全屏整屏（非工作区）：壁纸要盖到任务栏后面。
        Left = monitor.X; Top = monitor.Y; Width = monitor.Width; Height = monitor.Height;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowInterop.MakeClickThrough(_hwnd); // 置底 + 点击穿透 + NOACTIVATE
        };
        Visibility = Visibility.Hidden; // 无壁纸默认：不遮系统壁纸
    }

    /// <summary>真机兜底：WPF Visibility 在创建时序下可能不落 WS_VISIBLE（日志 Visible 但 IsWindowVisible=false），
    /// 且 WPF 可能在 SourceInitialized 后覆写 ex style（LAYERED 被抹）。每次可见性/样式变更后 Win32 层强制对齐。</summary>
    private void SyncNativeState()
    {
        if (_hwnd == IntPtr.Zero) return;
        ShowWindow(_hwnd, Visibility == Visibility.Visible ? SW_SHOWNOACTIVATE : SW_HIDE);
        // 只同步样式不置底（Z-order 由 host.BottomPair 统一管，避免与图标层置底互踩）。
        try { WindowInterop.ApplyClickThroughStyles(_hwnd); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Warning(ex, "壁纸窗样式同步失败"); }
    }

    /// <summary>M3-T6 同款：分辨率/排列变化后重定位到新整屏 rect。</summary>
    public void RepositionTo(MonitorInfo monitor)
    {
        Left = monitor.X; Top = monitor.Y;
        Width = monitor.Width; Height = monitor.Height;
    }

    /// <summary>应用壁纸配置。null/空路径/文件不存在 → 隐藏（系统壁纸透出）。
    /// T2 只接静态图；T3 扩展视频/GIF（按扩展名校正 Kind，防用户改扩展名）。</summary>
    public void SetWallpaper(WallpaperConfig? config)
    {
        if (config is null || string.IsNullOrWhiteSpace(config.Path) || !File.Exists(config.Path))
        {
            WallpaperImage.Source = null;
            Visibility = Visibility.Hidden;
            SyncNativeState();
            return;
        }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 读完即释放文件句柄（不锁用户壁纸文件）
            bmp.UriSource = new Uri(config.Path);
            bmp.EndInit();
            bmp.Freeze(); // 跨线程安全 + 省内存
            WallpaperImage.Source = bmp;
            Visibility = Visibility.Visible;
            SyncNativeState();
            // WPF 首帧布局可能再隐藏（真机：同步 ShowWindow 后仍 vis=False）→ 延迟一帧强制对齐。
            Dispatcher.BeginInvoke(new Action(SyncNativeState), System.Windows.Threading.DispatcherPriority.Loaded);
            Log.Information("壁纸已应用: {Path} ex=0x{E:X}", config.Path, WindowInterop.GetExtendedStyle(_hwnd));
        }
        catch (Exception ex)
        {
            // 文件损坏/格式不支持：不崩，回退无壁纸（日志可诊断）。
            Log.Warning(ex, "壁纸加载失败，回退系统壁纸：{Path}", config.Path);
            WallpaperImage.Source = null;
            Visibility = Visibility.Hidden;
            SyncNativeState();
        }
    }
}
