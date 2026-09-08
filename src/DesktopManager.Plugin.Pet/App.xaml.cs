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
        _window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false,
            AllowsTransparency = true, Background = Brushes.Transparent,
            Width = PetBrain.Size, Height = PetBrain.Size,
            Top = 400, Left = 300,
        };
        _visual = new TextBlock
        {
            FontSize = PetBrain.Size * 0.72,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Text = _renderer.IdleFace,
        };
        _window.Content = new Grid { Children = { _visual } };
        _window.SourceInitialized += (_, _) =>
        {
            if (_solo)
            {
                var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                _brain.SetBounds(new List<(Rect, bool)> { (vs, true) });
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

    // ---------- 行为主循环（UI 线程 30fps） ----------
    private void Step()
    {
        _brain.Step(_dragging);
        // 窗口跟随大脑位置（虚拟桌面坐标 = WPF 双屏坐标一致）
        _window!.Left = _brain.X;
        _window.Top = _brain.Y;
        // 姿态渲染
        var (face, flip, angle) = _brain.Pose;
        _visual!.Text = face;
        var rt = new ScaleTransform { ScaleX = _brain.Facing * (flip ? -1 : 1) };
        var rot = new RotateTransform(angle);
        _visual.RenderTransform = new TransformGroup { Children = { rt, rot } };
    }

    // ---------- 鼠标交互（拖拽 / 点击 / 双击） ----------
    private void HookMouse()
    {
        _window!.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2) { _clickArmed = false; _brain.Interact("double"); return; }
            _clickArmed = true;
            _clickOrigin = _window.PointToScreen(e.GetPosition(_window));
            _dragOffset = e.GetPosition(_window);
            _window.CaptureMouse();   // 96x96 小窗：不捕获则鼠标出窗即丢 Move/Up（拖不动的根因）
        };
        _window.MouseMove += (_, e) =>
        {
            if (Mouse.LeftButton != MouseButtonState.Pressed) return;
            if (!_clickArmed) return;
            var s = _window.PointToScreen(e.GetPosition(_window));
            if (Math.Abs(s.X - _clickOrigin.X) + Math.Abs(s.Y - _clickOrigin.Y) > 6)
            {
                _dragging = true;
                _brain.BeginDrag();
            }
            if (_dragging)
            {
                var p = _window.PointToScreen(e.GetPosition(_window));
                _brain.DragTo(p.X - _dragOffset.X, p.Y - _dragOffset.Y);
            }
        };
        _window.MouseLeftButtonUp += (_, e) =>
        {
            _window.ReleaseMouseCapture();
            if (_dragging)
            {
                _dragging = false;
                _brain.EndDrag();
            }
            else if (_clickArmed)
            {
                _brain.Interact(e.GetPosition(_window).Y < PetBrain.Size * 0.4 ? "head" : "body");
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
                _screens = mi.Monitors.Select(m => new Rect(m.X, m.Y, m.W, m.H)).ToList();
                _brain.SetBounds(mi.Monitors.Select(m => (new Rect(m.X, m.Y, m.W, m.H), m.IsPrimary)).ToList());
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
