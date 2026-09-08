using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopManager.Ipc;

namespace DesktopManager.Plugin.Pet;

/// <summary>M8-T2 桌面宠物插件。
/// 窗口 = 宠物本体（小尺寸，随行为移动），宿主挂桌面层（图标下）。
/// 行为状态机：idle/walk/climb/fall/drag/interact + 简单物理（重力/边界/攀爬）。
/// 渲染器抽象：本轮内置 Emoji 渲染器（CATS 字典姿态切换 + 变换动画），
/// Sprite/Live2D 渲染器为后续插槽（IPetRenderer 按素材目录自动选择）。
/// 协议与壁纸/图标层/Demo 插件同构：ready{hwnd} → PluginHello → MonitorsReq → Pause/Resume → Shutdown。</summary>
public partial class App : Application
{
    private Window? _window;
    private TextBlock? _visual;
    private Canvas? _canvas;
    private Microsoft.Web.WebView2.Wpf.WebView2? _web;   // Live2D 模式（素材存在时启用）
    private string? _modelPath;                          // .model3.json（null=emoji 模式）
    private bool _windowSized;   // MonitorsInfo 后窗口定位完成（Step 才开始动猫）
    private string? _lastState;  // Live2D 状态去重（motion 只在变化时切）
    private CancellationTokenSource? _cts;
    private readonly PetBrain _brain = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(33) }; // ~30fps
    private List<Rect> _screens = new();
    private bool _dragging;
    private Point _dragOffset;
    private bool _clickArmed;
    private Point _clickOrigin;

    // ---- 渲染器抽象（T2 内置 emoji；后续 Sprite/Live2D 实现 IPetRenderer 自动替换） ----
    private IPetRenderer _renderer = new EmojiPetRenderer();

    private bool _solo;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // --solo：脱离宿主独立调试（Live2D 渲染器开发主力模式）——系统虚拟屏当世界，跳过 IPC
        _solo = e.Args.Contains("--solo", StringComparer.OrdinalIgnoreCase);
        // 关键：窗口不动内容动——WPF 分层窗口逐帧移动必闪烁（DWM 合成空隙，真机）。
        // 窗口铺主屏透明；Background=null → 空白区域系统级鼠标穿透（layered 窗 alpha=0 穿透），
        // 只有猫 visual 的实心像素可命中（交互直达）。
        _window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false,
            AllowsTransparency = true, Background = null,
            Width = 1, Height = 1, Top = 0, Left = 0,
        };
        _canvas = new Canvas();
        // 渲染器选择：assets 目录有 .model3.json → Live2D（WebView2 透明）；否则 Emoji
        _modelPath = Live2DAvailability.FindModel(AppContext.BaseDirectory);
        if (_modelPath is not null)
        {
            _web = new Microsoft.Web.WebView2.Wpf.WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.Transparent,
                Width = PetBrain.Size * 1.6, Height = PetBrain.Size * 1.6,   // 模型取景大于判定框
            };
            _canvas.Children.Add(_web);
            _ = InitLive2DAsync();
        }
        else
        {
            _visual = new TextBlock
            {
                FontSize = PetBrain.Size * 0.72,
                Text = _renderer.IdleFace,
            };
            _canvas.Children.Add(_visual);
        }
        _window.Content = _canvas;
        _window.SourceInitialized += (_, _) =>
        {
            if (_solo)
            {
                var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight - 2);
                _window.Left = vs.Left; _window.Top = vs.Top;
                _window.Width = vs.Width; _window.Height = vs.Height;
                _brain.SetBounds(new List<(Rect, bool)> { (new Rect(0, 0, vs.Width, vs.Height), true) });
                _windowSized = true;
                return;
            }
            var hwnd = new WindowInteropHelper(_window).Handle;
            IpcWriter.Write(Console.OpenStandardOutput(), new Ready { Hwnd = hwnd.ToInt64() });
            IpcWriter.Write(Console.OpenStandardOutput(), new PluginHello
            {
                PluginId = PetId, Name = "桌面宠物", Version = "1.0.0",
            });
            IpcWriter.Write(Console.OpenStandardOutput(), new MonitorsReq());
            StartStdinLoop();
        };
        HookMouse();
        _window.Show();
        _tick.Tick += (_, _) => Step();
        _tick.Start();
    }

    public const string PetId = "com.desktopmanager.pet";

    /// <summary>Live2D 初始化：加载本地 HTML（内嵌 pixi + live2d-display，CDN 拉库）。</summary>
    private async Task InitLive2DAsync()
    {
        try
        {
            await _web!.EnsureCoreWebView2Async();
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            var html = Path.Combine(AppContext.BaseDirectory, "Assets", "live2d.html");
            _web.CoreWebView2.Navigate(new Uri(html + "?model=" + Uri.EscapeDataString(_modelPath!)).AbsoluteUri);
            _web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                // JS 上报渲染器状态（live2d/fallback）——fallback 时可切回 emoji
                var kind = e.TryGetWebMessageAsString();
                Console.Error.WriteLine("[pet] renderer: " + kind);
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[pet] WebView2 init fail: " + ex.Message);
        }
    }

    /// <summary>向 Live2D 页面广播状态（映射 motion）/朝向。</summary>
    private void PushStateToWeb(string state)
    {
        if (_web?.CoreWebView2 is null) return;
        try
        {
            _web.CoreWebView2.PostWebMessageAsJson(
                System.Text.Json.JsonSerializer.Serialize(new { type = "state", state }));
        }
        catch { }
    }

    // ---------- 行为主循环（UI 线程 30fps） ----------
    private void Step()
    {
        if (!_windowSized) return;
        _brain.Step(_dragging);
        // 内容动窗口不动（窗口逐帧移动=分层窗闪烁，真机教训）
        var fx = _web is not null ? _brain.X - PetBrain.Size * 0.3 : _brain.X;   // Live2D 取景框居中对齐判定框
        var fy = _web is not null ? _brain.Y - PetBrain.Size * 0.3 : _brain.Y;
        if (_visual is not null) { Canvas.SetLeft(_visual, _brain.X); Canvas.SetTop(_visual, _brain.Y); }
        if (_web is not null) { Canvas.SetLeft(_web, fx); Canvas.SetTop(_web, fy); }
        // 状态推送给 Live2D（映射 motion）
        var st = _brain.Current.ToString().ToLowerInvariant();
        if (st != _lastState) { _lastState = st; PushStateToWeb(st); }
        // Emoji 姿态渲染
        if (_visual is not null)
        {
            var (face, flip, angle) = _brain.Pose;
            _visual.Text = face;
            var rt = new ScaleTransform { ScaleX = _brain.Facing };
            var rot = new RotateTransform(angle);
            _visual.RenderTransform = new TransformGroup { Children = { rt, rot } };
        }
    }

    // ---------- 鼠标交互（拖拽 / 点击 / 双击） ----------
    private void HookMouse()
    {
        // 坐标系：窗口=主屏全屏（0,0 起），Brain 用窗口内坐标——鼠标事件坐标直通
        _window!.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2) { _clickArmed = false; _brain.Interact("double"); return; }
            _clickArmed = true;
            _clickOrigin = e.GetPosition(_window);
            // 抓取偏移 = 点击点相对猫（若相对窗口，差值含猫位置 → DragTo 瞬移左上角，真机）
            var cp = e.GetPosition(_window);
            _dragOffset = new Point(cp.X - _brain.X, cp.Y - _brain.Y);
        };
        _window.MouseMove += (_, e) =>
        {
            if (Mouse.LeftButton != MouseButtonState.Pressed || !_clickArmed) return;
            var p = e.GetPosition(_window);
            if (!_dragging && Math.Abs(p.X - _clickOrigin.X) + Math.Abs(p.Y - _clickOrigin.Y) > 6)
                _dragging = true; _brain.BeginDrag();
                _window.Background = System.Windows.Media.Brushes.Transparent;   // 拖拽中全窗命中（null 穿透会断事件）
            if (_dragging)
            {
                _brain.DragTo(p.X - _dragOffset.X, p.Y - _dragOffset.Y);
                if (_visual is not null) { Canvas.SetLeft(_visual, _brain.X); Canvas.SetTop(_visual, _brain.Y); }
                if (_web is not null) { Canvas.SetLeft(_web, _brain.X - PetBrain.Size * 0.3); Canvas.SetTop(_web, _brain.Y - PetBrain.Size * 0.3); }
            }
        };
        _window.MouseLeftButtonUp += (_, e) =>
        {
            if (_dragging)
            {
                _dragging = false;
                _brain.EndDrag();
                _window.Background = null;   // 恢复空白穿透
            }
            else if (_clickArmed)
            {
                // 命中判断：点击点相对猫位置（猫区域才收到事件——null 背景穿透保证）
                var p = e.GetPosition(_window);
                var relY = p.Y - _brain.Y;
                _brain.Interact(relY < PetBrain.Size * 0.4 ? "head" : "body");
            }
            _clickArmed = false;
        };
    }

    // ---------- 宿主 IPC ----------
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
                    if (msg is null) break;
                    await Dispatcher.InvokeAsync(() => Handle(msg));
                }
            }
            catch { }
            await Dispatcher.InvokeAsync(() => Shutdown(0));
        });
    }

    private void Handle(IpcMessage msg)
    {
        switch (msg)
        {
            case MonitorsInfo mi:
                {
                    _screens = mi.Monitors.Select(m => new Rect(m.X, m.Y, m.W, m.H)).ToList();
                    var p = mi.Monitors.FirstOrDefault(m => m.IsPrimary) ?? mi.Monitors[0];
                    Dispatcher.BeginInvoke(() =>
                    {
                        _window!.Left = p.X; _window!.Top = p.Y;
                        _window!.Width = p.W; _window!.Height = p.H - 2;   // 底缝防任务栏全屏检测
                        _brain.SetBounds(new List<(Rect, bool)> { (new Rect(0, 0, p.W, p.H - 2), true) });
                        _windowSized = true;
                    });
                }
                break;
            case Pause:
                Dispatcher.BeginInvoke(() =>
                {
                    _tick.Stop();
                    _brain.Sleep();
                    _visual!.Text = _renderer.SleepFace;
                    _visual.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.15, TimeSpan.FromSeconds(1)));
                });
                break;
            case Resume:
                Dispatcher.BeginInvoke(() =>
                {
                    _visual!.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.6)));
                    _brain.Wake();
                    _tick.Start();
                });
                break;
            case DesktopManager.Ipc.PluginMenuItemClick mc:
                Dispatcher.BeginInvoke(() =>
                {
                    if (mc.ItemId == "summon") { _brain.Summon(); }
                    else if (mc.ItemId == "sleep") { _brain.Sleep(); _visual!.Text = _renderer.SleepFace; }
                });
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
