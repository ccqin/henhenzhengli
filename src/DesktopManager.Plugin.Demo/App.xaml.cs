using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DesktopManager.Ipc;

namespace DesktopManager.Plugin.Demo;

/// <summary>M8-T1 空壳测试插件：透明全屏窗口 + 随机漂移的方块。
/// 验证宿主全链路：发现 → 启动 → ready{hwnd} → PluginHello → 挂桌面层 Z 序 →
/// MonitorsReq/ConfigGet → Pause/Resume → Shutdown。可交互模式（点击穿透=false）。
/// 协议与壁纸/图标层子进程同构（stdin JSON 行 + stdout ready 上报）。 </summary>
public partial class App : Application
{
    private Window? _window;
    private CancellationTokenSource? _cts;
    private Rect? _lastKnownMonitor;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            // 宿主挂载后定位全虚拟桌面；此处先 0 尺寸（等 MonitorsInfo 或宿主 SetWindowPos）
            Width = 1, Height = 1,
            IsHitTestVisible = false, // demo 方块本身不交互（宿主清单 clickThrough=false 由宿主决定穿透样式）
        };
        _window.SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            IpcWriter.Write(Console.OpenStandardOutput(), new Ready { Hwnd = hwnd.ToInt64() });
            // 插件握手：声明身份（宿主校验清单一致）
            IpcWriter.Write(Console.OpenStandardOutput(), new PluginHello
            {
                PluginId = "com.desktopmanager.demo",
                Name = "Demo 漂移方块",
                Version = "1.0.0",
            });
            // 拉取屏幕拓扑（验证 MonitorsReq/MonitorsInfo 回路）
            IpcWriter.Write(Console.OpenStandardOutput(), new MonitorsReq());
            StartStdinLoop();
        };
        _window.Show();
        _ = Task.Run(DriftLoop);
    }

    /// <summary>漂移方块渲染：主屏中 100x100 方块随机走动（验证桌面层挂载/重排后仍可见）。</summary>
    private async Task DriftLoop()
    {
        var rect = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(160, 0, 174, 255)), Width = 100, Height = 100 };
        double x = 200, y = 200, dx = 60, dy = 40; // 像素/秒
        var last = DateTime.UtcNow;
        await Dispatcher.InvokeAsync(() => _window!.Content = rect);
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            var dt = (now - last).TotalSeconds; last = now;
            var area = _lastKnownMonitor;
            if (area is { } a)
            {
                x += dx * dt; y += dy * dt;
                if (x < a.Left || x + 100 > a.Right) { dx = -dx; x = Math.Clamp(x, a.Left, a.Right - 100); }
                if (y < a.Top || y + 100 > a.Bottom) { dy = -dy; y = Math.Clamp(y, a.Top, a.Bottom - 100); }
                Canvas.SetLeft(rect, x - a.Left);
                Canvas.SetTop(rect, y - a.Top);
            }
        };
        timer.Start();
        await Task.CompletedTask;
    }

    private void StartStdinLoop()
    {
        _cts = new CancellationTokenSource();
        var reader = IpcReader.OpenReader(Console.OpenStandardInput());
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var msg = await IpcReader.ReadAsync(reader, _cts.Token);
                    if (msg is null) break; // 宿主已死
                    await Dispatcher.InvokeAsync(() => Handle(msg));
                }
            }
            catch { /* 流关闭 */ }
            await Dispatcher.InvokeAsync(() => Shutdown(0));
        });
    }

    private void Handle(IpcMessage msg)
    {
        switch (msg)
        {
            case MonitorsInfo mi when mi.Monitors.Count > 0:
                var prim = mi.Monitors.FirstOrDefault(m => m.IsPrimary) ?? mi.Monitors[0];
                _lastKnownMonitor = new Rect(prim.X, prim.Y, prim.W, prim.H);
                Dispatcher.BeginInvoke(() =>
                {
                    _window!.Left = prim.X; _window!.Top = prim.Y;
                    _window!.Width = prim.W; _window!.Height = prim.H;
                });
                break;

            case Pause:
                Dispatcher.BeginInvoke(() => { if (_window!.Content is Rectangle r) r.Opacity = 0.2; });
                break;

            case Resume:
                Dispatcher.BeginInvoke(() => { if (_window!.Content is Rectangle r) r.Opacity = 1; });
                break;

            case DesktopManager.Ipc.Shutdown:
                Shutdown(0);
                break;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cts?.Cancel();
        base.OnExit(e);
    }
}
