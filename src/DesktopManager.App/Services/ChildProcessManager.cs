using System.Diagnostics;
using DesktopManager.Ipc;
using Serilog;

namespace DesktopManager.App.Services;

/// <summary>M6：子进程管理器。启动渲染子进程（stdin/stdout JSON 行协议），
/// 等待 Ready{hwnd} 上报，之后双向通信。进程异常退出上报事件（调用方可重启）。</summary>
public sealed class ChildProcessManager : IDisposable
{
    private Process? _process;
    private IpcChannel? _channel;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>子进程上报的消息（UI 线程之外触发，调用方自行调度）。</summary>
    public event Action<IpcMessage>? MessageReceived;

    /// <summary>子进程退出（含正常 Stop）。参数=退出码。</summary>
    public event Action<int>? Exited;

    public string MonitorId { get; }
    public long Hwnd { get; private set; }
    public bool IsAlive => _process is { HasExited: false };

    public ChildProcessManager(string monitorId) => MonitorId = monitorId;

    /// <summary>启动子进程并等待 Ready{hwnd}。超时抛 TimeoutException。</summary>
    public async Task<long> StartAsync(string exePath, string args, int readyTimeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(exePath, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
        };
        _process = Process.Start(psi) ?? throw new InvalidOperationException("子进程启动失败: " + exePath);
        _channel = new IpcChannel(_process);
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            try { Exited?.Invoke(_process.HasExited ? _process.ExitCode : -1); }
            catch { Exited?.Invoke(-1); }
        };

        // Ready 是子进程第一条 stdout 消息，先同步读它，再进入常驻接收循环。
        // ConfigureAwait(false)：调用方（UI 线程）会 GetResult() 同步等待，若续体回 UI 上下文 = 死锁。
        var first = await _channel.ReceiveAsync().ConfigureAwait(false);
        if (first is not Ready ready)
            throw new InvalidOperationException($"子进程首条消息不是 Ready: {first?.GetType().Name ?? "EOF"}");
        Hwnd = ready.Hwnd;

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => _channel.RunAsync(
            m => MessageReceived?.Invoke(m),
            err => Log.Debug("子进程 stderr[{Mon}]: {Err}", MonitorId, err),
            _cts.Token));
        Log.Information("子进程就绪：{Mon} exe={Exe} hwnd={Hwnd}", MonitorId, exePath, Hwnd);
        return Hwnd;
    }

    public void Send(IpcMessage message)
    {
        if (_channel is null || !IsAlive) return;
        try { _channel.Send(message); }
        catch (Exception ex) { Log.Warning(ex, "IPC 发送失败：{Mon} {Type}", MonitorId, message.GetType().Name); }
    }

    /// <summary>请求正常退出（Shutdown 指令 + 关 stdin + 等 3s + Kill 兜底）。</summary>
    public void Stop()
    {
        if (_disposed) return;
        try
        {
            Send(new DesktopManager.Ipc.Shutdown());
            _process?.StandardInput.Close();
            _process?.WaitForExit(3000);
            if (_process is { HasExited: false }) _process.Kill();
        }
        catch { /* 已退出 */ }
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _channel?.Dispose();
        try { if (_process is { HasExited: false }) _process.Kill(); } catch { }
        _process?.Dispose();
    }
}
