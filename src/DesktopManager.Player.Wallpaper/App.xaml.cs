using System.IO;
using System.Windows;
using DesktopManager.Ipc;

namespace DesktopManager.Player.Wallpaper;

/// <summary>壁纸子进程入口。stdout 上报 Ready{hwnd}，stdin 收主进程指令。
/// 主进程负责 SetParent 到 WorkerW；stdin EOF = 主进程已死 → 自动退出。</summary>
public partial class App : Application
{
    private WallpaperWindow? _window;
    private CancellationTokenSource? _cts;

    protected override void OnStartup(StartupEventArgs e)
    {
        // GPU 优化①（已撤销）：Timeline.DesiredFrameRate=30 会连带限制 MediaElement 内部的
        // MediaTimeline → 视频从原生 60fps 被强降到 30fps = 明显卡顿（真机验证）。
        // 壁纸播放器保持 60fps 不动；仅图标层子进程限 30fps（静态内容无感知）。

        // 可选软渲染（诊断开关）：默认硬件渲染（M5 顶层形态验证过）。
        if (Environment.GetEnvironmentVariable("DESKTOPMGR_SWRENDER") == "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode =
                System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        int x = GetArg(e.Args, "--monitor-x", 0);
        int y = GetArg(e.Args, "--monitor-y", 0);
        int w = GetArg(e.Args, "--monitor-w", 1920);
        int h = GetArg(e.Args, "--monitor-h", 1080);

        _window = new WallpaperWindow(x, y, w, h);
        _window.SourceInitialized += (_, _) =>
        {
            IpcWriter.Write(Console.OpenStandardOutput(),
                new Ready { Hwnd = new System.Windows.Interop.WindowInteropHelper(_window).Handle.ToInt64() });
            StartStdinLoop();
        };
        // 关键：不能提前 Show——已显示的窗口被跨进程 SetParent 进 WorkerW 后 DWM 可能放弃
        // 合成（真机：窗口 vis=True 但纯黑）。EnsureHandle 只创建 hwnd 不显示，等主进程
        // SetParent 完成后发 Show 指令再显示。
        _window.VideoOversized += (w, h, sw, sh) =>
            IpcWriter.Write(Console.OpenStandardOutput(), new Error
            {
                Message = $"GPU提示：视频 {w}x{h} 高于屏幕 {sw}x{sh}，解码后再缩小浪费 GPU。建议使用 {sw}x{sh} 分辨率视频。",
            });
        _window.VideoPositionChanged += pos =>
            IpcWriter.Write(Console.OpenStandardOutput(), new VideoPositionReport { PositionMs = pos });
        new System.Windows.Interop.WindowInteropHelper(_window).EnsureHandle();
    }

    private void StartStdinLoop()
    {
        _cts = new CancellationTokenSource();
        var stdin = Console.OpenStandardInput();
        var reader = IpcReader.OpenReader(stdin);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var msg = await IpcReader.ReadAsync(reader, _cts.Token);
                    if (msg is null) break; // stdin EOF：主进程已死
                    await Dispatcher.InvokeAsync(() => Handle(msg));
                }
            }
            catch { /* 流关闭 */ }
            await Dispatcher.InvokeAsync(() => Shutdown(0));
        });
    }

    private void Handle(IpcMessage msg)
    {
        if (_window is null) return;
        try
        {
            switch (msg)
            {
                case SetWallpaper w:
                    _window.ApplyWallpaper(w);
                    break;
                case SetPosition p:
                    _window.RepositionTo(p.X, p.Y, p.W, p.H);
                    break;
                case Pause: _window.Pause(); break;
                case Resume: _window.Resume(); break;
                case Show: _window.ShowLayer(); break;
                case SetVideoPosition vp: _window.Seek(vp.PositionMs); break;
                case DesktopManager.Ipc.Shutdown: Shutdown(0); break;
            }
        }
        catch (Exception ex)
        {
            try { IpcWriter.Write(Console.OpenStandardOutput(), new Error { Message = ex.ToString() }); } catch { }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cts?.Cancel();
        base.OnExit(e);
    }

    private static int GetArg(string[] args, string name, int def)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name && int.TryParse(args[i + 1], out var v)) return v;
        return def;
    }
}
