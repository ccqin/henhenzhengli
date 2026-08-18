using System.IO;
using System.Windows;
using System.Windows.Threading;
using DesktopManager.Core.Models;
using DesktopManager.Core.Services;
using DesktopManager.Ipc;
using DesktopManager.Native;

namespace DesktopManager.Player.Icons;

/// <summary>图标层子进程入口。stdout 上报 Ready{hwnd}，stdin 收主进程指令。
/// 布局变更防抖上报 LayoutChanged（主进程聚合持久化）；跨屏操作经 ICrossScreenHost 转 IPC 中转。</summary>
public partial class App : Application, ICrossScreenHost
{
    private IconLayerWindow? _window;
    private MonitorInfo _monitor = new("", "", 0, 0, 0, 0, 0, 0, 0, 0, false);
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _layoutDebounce;
    private readonly object _writeLock = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // 可选软渲染（诊断开关）：默认硬件渲染（M5 顶层形态验证过）。
        if (Environment.GetEnvironmentVariable("DESKTOPMGR_SWRENDER") == "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode =
                System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var mon = new MonitorInfo(
            GetArg(e.Args, "--device", ""),
            GetArg(e.Args, "--monitor-id", ""),
            GetArg(e.Args, "--x", 0), GetArg(e.Args, "--y", 0),
            GetArg(e.Args, "--w", 1920), GetArg(e.Args, "--h", 1080),
            GetArg(e.Args, "--work-x", 0), GetArg(e.Args, "--work-y", 0),
            GetArg(e.Args, "--work-w", 1920), GetArg(e.Args, "--work-h", 1080),
            GetArg(e.Args, "--primary", 1) == 1);
        _monitor = mon;

        _window = new IconLayerWindow(mon, Array.Empty<FenceConfig>(), Array.Empty<IconPosition>())
        {
            Host = this
        };
        IconLayerWindow.OpenReported += (path, err) =>
            Reply(err is null ? new IconOpened { Path = path } : new Error { Message = $"打开失败 {path}: {err}" });
        IconLayerWindow.AuditReported += (kind, action, detail, arg) =>
        {
            if (kind == "fence")
                Reply(new FenceAction { Action = action, Title = detail });
            else
                Reply(new IconAction { Action = action, Path = arg ?? "", Detail = detail });
        };
        _window.LayoutChanged += OnLayoutChangedLocal;

        // EnsureHandle：创建 hwnd（触发 SourceInitialized）但不显示——等主进程 SetParent 后发 Show。
        var hwnd = new System.Windows.Interop.WindowInteropHelper(_window).EnsureHandle();
        IpcWriter.Write(Console.OpenStandardOutput(), new Ready { Hwnd = hwnd.ToInt64() });
        StartStdinLoop();
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
                case Show: _window.Show(); break;
                case SetAppearance ap:
                    _window.LabelStyle = ap.LabelStyle;
                    _window.IconSize = ap.IconSize;
                    break;
                case SetMenu m:
                    _window.ApplyMenu(m);
                    break;
                case SetPosition p:
                    _window.RepositionTo(_monitor with { WorkX = p.X, WorkY = p.Y, WorkWidth = p.W, WorkHeight = p.H });
                    break;
                case SetFences f:
                    _window.ApplyFences(f.Fences.Select(FromDto).ToList());
                    break;
                case SetIcons s:
                    _window.ApplySnapshot(s.Items.Select(FromDto).ToList());
                    break;
                case ApplyDiff d:
                    _window.ApplyDiff(new DesktopDiff(
                        d.Added.Select(FromDto).ToList(),
                        d.Removed.Select(p => new IconItem(p, p)).ToList()));
                    break;
                case ClearSelection: _window.ClearLocalSelection(); break;
                case ExportIcon ex:
                    var item = _window.ExportIcon(ex.Path);
                    Reply(new ExportIconData
                    {
                        Found = item is not null,
                        Path = item?.FilePath ?? ex.Path,
                        Name = item?.DisplayName ?? "",
                        X = item?.X ?? 0,
                        Y = item?.Y ?? 0,
                    });
                    break;
                case ImportIcon im:
                    _window.ImportLoose(new IconItem(im.Path, im.Name), new Point(im.X, im.Y));
                    break;
                case ExportFence ef:
                    var cfg = _window.ExportFence(ef.FenceId);
                    Reply(new ExportFenceData { Found = cfg is not null, Fence = ToDto(cfg) });
                    break;
                case ImportFence imf:
                    _window.ImportFence(FromDto(imf.Fence) with { X = imf.X, Y = imf.Y });
                    break;
                case MoveFencePos mv:
                    _window.MoveFence(mv.FenceId, new Point(mv.X, mv.Y));
                    break;
                case DesktopManager.Ipc.Shutdown: Shutdown(0); break;
            }
        }
        catch (Exception ex)
        {
            Reply(new Error { Message = ex.ToString() });
        }
    }

    // ---------- ICrossScreenHost：跨屏操作 → IPC 请主进程中转 ----------

    void ICrossScreenHost.TransferLoose(string path, Point pos) =>
        Reply(new TransferLooseReq { Path = path, TargetMonitorId = _monitor.PersistentId, X = pos.X, Y = pos.Y });

    void ICrossScreenHost.TransferFence(string fenceId, Point pos) =>
        Reply(new TransferFenceReq { FenceId = fenceId, TargetMonitorId = _monitor.PersistentId, X = pos.X, Y = pos.Y });

    void ICrossScreenHost.ClearAllSelection() =>
        Reply(new ClearSelectionExcept { MonitorId = _monitor.PersistentId });

    // ---------- 布局上报（防抖 500ms） ----------

    private void OnLayoutChangedLocal()
    {
        if (_layoutDebounce is null)
        {
            _layoutDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _layoutDebounce.Tick += (_, _) =>
            {
                _layoutDebounce.Stop();
                if (_window is null) return;
                var (fences, positions) = _window.BuildLayout();
                Reply(new LayoutChanged
                {
                    Fences = fences.Select(ToDto).ToList(),
                    Positions = positions.Select(p => new IconPosDto { Path = p.FilePath, X = p.X, Y = p.Y }).ToList(),
                });
            };
        }
        _layoutDebounce.Stop();
        _layoutDebounce.Start();
    }

    // ---------- DTO 映射 ----------

    private static IconItem FromDto(IconDto d) => new(d.Path, d.Name, d.X, d.Y);

    private static FenceConfig FromDto(FenceDto f) => new()
    {
        Id = f.Id, Title = f.Title, X = f.X, Y = f.Y, W = f.W, H = f.H, Folded = f.Collapsed,
        IconFilePaths = f.IconPaths.ToList(),
    };

    private static FenceDto ToDto(FenceConfig? f) => new()
    {
        Id = f?.Id ?? "", Title = f?.Title ?? "", X = f?.X ?? 0, Y = f?.Y ?? 0,
        W = (int)(f?.W ?? 0), H = (int)(f?.H ?? 0), Collapsed = f?.Folded ?? false,
        IconPaths = f?.IconFilePaths.ToList() ?? [],
    };

    private void Reply(IpcMessage msg)
    {
        lock (_writeLock)
        {
            try { IpcWriter.Write(Console.OpenStandardOutput(), msg); }
            catch { /* stdout 已关（主进程已死） */ }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cts?.Cancel();
        base.OnExit(e);
    }

    private static string GetArg(string[] args, string name, string def)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return def;
    }

    private static int GetArg(string[] args, string name, int def)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name && int.TryParse(args[i + 1], out var v)) return v;
        return def;
    }
}
