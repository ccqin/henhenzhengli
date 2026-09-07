using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
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
    private List<Rect> _allMonitors = new();

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
        DriftLoop();
    }

    /// <summary>漂移方块渲染：主屏中 100x100 方块随机走动（验证桌面层挂载/重排后仍可见）。
    /// 注意：DispatcherTimer 必须在 UI 线程创建（后台线程的 Dispatcher 不泵消息，Tick 永不触发——真机教训）。</summary>
    private void DriftLoop()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var rect = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(160, 0, 174, 255)), Width = 100, Height = 100 };
            var canvas = new Canvas();
            canvas.Children.Add(rect);
            _window!.Content = canvas;
            double x = 200, y = 200, dx = 60, dy = 40; // 像素/秒
            var last = DateTime.UtcNow;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                var now = DateTime.UtcNow;
                var dt = (now - last).TotalSeconds; last = now;
                if (_lastKnownMonitor is { } a)
                {
                    x += dx * dt; y += dy * dt;
                    if (x < a.Left || x + 100 > a.Right) { dx = -dx; x = Math.Clamp(x, a.Left, a.Right - 100); }
                    if (y < a.Top || y + 100 > a.Bottom) { dy = -dy; y = Math.Clamp(y, a.Top, a.Bottom - 100); }
                    Canvas.SetLeft(rect, x - a.Left);
                    Canvas.SetTop(rect, y - a.Top);
                }
            };
            timer.Start();
        });
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
                // 漂移域 = 全部屏并集（虚拟桌面）——方块可跨屏移动；窗口本身由宿主挂全虚拟桌面
                var u = mi.Monitors[0];
                _allMonitors = mi.Monitors.Select(m => new Rect(m.X, m.Y, m.W, m.H)).ToList();
                foreach (var m in mi.Monitors.Skip(1))
                    u = Rect.Union(u, new Rect(m.X, m.Y, m.W, m.H));
                _lastKnownMonitor = u;
                Dispatcher.BeginInvoke(() =>
                {
                    _window!.Left = u.Left; _window!.Top = u.Top;
                    _window!.Width = u.Width; _window!.Height = u.Height - 2; // 底缝防任务栏全屏检测
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
