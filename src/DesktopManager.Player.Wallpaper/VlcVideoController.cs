using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using LibVLCSharp.Shared;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace DesktopManager.Player.Wallpaper;

/// <summary>LibVLC 视频控制器（GPU 第三梯队）：视频经 DirectX 直渲染到 WS_CHILD 子 HWND，
/// 完全跳过 WPF 合成器（MediaElement 路径 GPU ~18% 的主因即合成器，预期降至 3-5%）。
/// 子 HWND 布局与旧 MediaElement 的 Canvas 布局 1:1：组模式负偏移放置，超出父客户区被 Win32 天然裁剪。
/// LibVLC 3.x 在 UI 线程 Stop/Dispose 有死锁风险 → 播放器释放一律走后台线程，按需重建。</summary>
internal sealed class VlcVideoController : IDisposable
{
    private const int WS_CHILD = 0x40000000;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const string HostClassName = "DMVlcVideoHost"; // 自有窗口类：黑底，避免 static 类默认灰底闪色

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
    }

    [DllImport("user32.dll")] private static extern ushort RegisterClassW(ref WNDCLASSW wc);
    // CharSet 必须 Unicode：className 传给 CreateWindowExW（W 版），ANSI marshal 会把类名变乱码
    // → 1407 找不到类 → 子窗口创建失败 → set_hwnd(0) → VLC 自建弹窗（真机教训）
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int i);

    private static readonly IntPtr DefWindowProcPtr =
        GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW");
    private static readonly IntPtr BlackBrush = GetStockObject(4); // BLACK_BRUSH

    private readonly Window _owner;
    private readonly IntPtr _hInst;
    private IntPtr _hostHwnd;
    private LibVLC? _libVLC;
    private VlcMediaPlayer? _player;

    /// <param name="owner">壁纸窗口（须已创建 HWND，即 SourceInitialized/EnsureHandle 之后）。</param>
    public VlcVideoController(Window owner)
    {
        _owner = owner;
        _hInst = GetModuleHandleW(null);
        var wc = new WNDCLASSW
        {
            lpfnWndProc = DefWindowProcPtr,
            hInstance = _hInst,
            hbrBackground = BlackBrush,
            lpszClassName = HostClassName,
        };
        if (RegisterClassW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /*ERROR_CLASS_HAS_ALREADY_BEEN*/)
            throw new InvalidOperationException($"RegisterClassW 失败 {Marshal.GetLastWin32Error()}");
        RecreateHost();
    }

    /// <summary>重建嵌入宿主子窗口。切换壁纸时旧播放器的 vout 还在后台拆除，
    /// 新播放器若复用同一 HWND 会与残骸争抢 → libvlc 弃嵌入自建顶层弹窗（真机：桌面上浮出 VLC 窗口）。
    /// 销毁旧宿主可连带拆掉其下的 VLC vout 窗口树，新播放器拿到干净宿主必能嵌入。</summary>
    private void RecreateHost()
    {
        if (_hostHwnd != IntPtr.Zero) DestroyWindow(_hostHwnd); // 须在创建线程（UI 线程）调用
        var parent = new WindowInteropHelper(_owner).Handle;
        _hostHwnd = CreateWindowExW(0, HostClassName, "", WS_CHILD, 0, 0, 0, 0,
            parent, IntPtr.Zero, _hInst, IntPtr.Zero);
        if (_hostHwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowExW 失败 {Marshal.GetLastWin32Error()}");
        // 先铺满父客户区：vout 启动时 0x0 宿主同样会被弃嵌入；组模式偏移由随后的 ApplyPlacement 矫正
        if (GetClientRect(parent, out var rc) && rc.right > 0 && rc.bottom > 0)
            MoveWindow(_hostHwnd, 0, 0, rc.right, rc.bottom, repaint: false);
    }

    /// <summary>是否已下发视频内容（替代旧 MediaElement.Source 判空）。</summary>
    public bool HasContent { get; private set; }

    /// <summary>播放视频（自动循环）。切换视频 = 重建宿主子窗口 + 后台释放旧播放器。</summary>
    public void Play(string path)
    {
        if (_libVLC is null)
        {
            LibVLCSharp.Shared.Core.Initialize(null); // 显式加载 libvlc.dll（幂等；null = 应用目录探测）
            _libVLC = new LibVLC("--no-osd", "--no-audio");
        }
        ReplacePlayer();
        RecreateHost();
        var player = new VlcMediaPlayer(_libVLC)
        {
            Hwnd = _hostHwnd,
            EnableHardwareDecoding = true,
            Volume = 0, // --no-audio 之外显式静音双保险（同旧 MediaElement.Volume=0）
        };
        _player = player;
        using var media = new Media(_libVLC, path);
        media.AddOption("input-repeat=65535");      // 循环（替代 MediaEnded → Position=0）
        media.AddOption(":no-video-title-show");    // 不在画面上叠加文件名
        HasContent = true;
        ShowWindow(_hostHwnd, SW_SHOW);
        player.Play(media);
    }

    /// <summary>兜底尺寸：从未布局过（0x0）时先铺满父客户区，保证嵌入窗口对 libvlc 有效。</summary>
    public void EnsureSized()
    {
        if (!GetClientRect(new WindowInteropHelper(_owner).Handle, out var rc)) return;
        if (rc.right > 0 && rc.bottom > 0)
            MoveWindow(_hostHwnd, 0, 0, rc.right, rc.bottom, repaint: false);
    }

    public void Pause()
    {
        try { _player?.Pause(); } catch { }
    }

    public void Resume()
    {
        try { _player?.Play(); } catch { }
    }

    public void Seek(double positionMs)
    {
        try { if (_player is not null) _player.Time = (long)positionMs; } catch { }
    }

    /// <summary>当前播放位置 ms（无媒体/失败返回 0；VideoPositionReport 用）。</summary>
    public double PositionMs
    {
        get
        {
            try { var t = _player?.Time ?? -1; return t < 0 ? 0 : t; } catch { return 0; }
        }
    }

    /// <summary>视频原始分辨率（vout 未就绪返回 0x0；替代 NaturalVideoWidth/Height，超屏检测用）。</summary>
    public (uint W, uint H) VideoSize
    {
        get
        {
            try
            {
                if (_player is null) return (0, 0);
                uint w = 0, h = 0;
                _player.Size(0, ref w, ref h);
                return (w, h);
            }
            catch { return (0, 0); }
        }
    }

    /// <summary>按 DIP 矩形布置子 HWND（PerMonitorV2 → 内部换算物理像素）。
    /// 等价于旧代码对 MediaElement 的 Canvas.SetLeft/SetTop + Width/Height。</summary>
    public void UpdatePlacement(double xDip, double yDip, double wDip, double hDip)
    {
        if (_hostHwnd == IntPtr.Zero) return;
        double s = VisualTreeHelper.GetDpi(_owner).DpiScaleX;
        MoveWindow(_hostHwnd, (int)Math.Round(xDip * s), (int)Math.Round(yDip * s),
            (int)Math.Ceiling(wDip * s), (int)Math.Ceiling(hDip * s), repaint: false);
    }

    public void SetVisible(bool visible) => ShowWindow(_hostHwnd, visible ? SW_SHOW : SW_HIDE);

    public void Stop()
    {
        HasContent = false;
        SetVisible(false);
        ReplacePlayer();
    }

    public void Dispose()
    {
        Stop();
        try { _libVLC?.Dispose(); } catch { }
        _libVLC = null;
        if (_hostHwnd != IntPtr.Zero) DestroyWindow(_hostHwnd); // 须在创建线程（UI 线程）调用
    }

    private void ReplacePlayer()
    {
        var old = _player;
        _player = null;
        if (old is not null)
            _ = Task.Run(() => { try { old.Dispose(); } catch { } });
    }
}
