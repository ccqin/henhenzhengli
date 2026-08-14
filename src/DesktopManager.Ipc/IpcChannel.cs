using System.Diagnostics;
using System.IO;

namespace DesktopManager.Ipc;

/// <summary>双向 IPC 通道：封装子进程的 stdin（写）+ stdout（读）。</summary>
public sealed class IpcChannel : IDisposable
{
    private readonly Stream _stdin;
    private readonly StreamReader _stdout;
    private readonly object _writeLock = new();

    public Process Process { get; }

    public IpcChannel(Process process)
    {
        Process = process;
        _stdin = process.StandardInput.BaseStream;
        _stdout = process.StandardOutput;
    }

    public void Send(IpcMessage message)
    {
        lock (_writeLock)
        {
            IpcWriter.Write(_stdin, message);
        }
    }

    /// <summary>读一条子进程消息；stdout 关闭返回 null。</summary>
    public Task<IpcMessage?> ReceiveAsync(CancellationToken ct = default) =>
        IpcReader.ReadAsync(_stdout, ct);

    /// <summary>后台循环读子进程 stdout。返回的 Task 在流结束时完成。</summary>
    public async Task RunAsync(
        Action<IpcMessage> onMessage,
        Action<string>? onStdError = null,
        CancellationToken ct = default)
    {
        if (onStdError is not null && Process.StartInfo.RedirectStandardError)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await Process.StandardError.ReadLineAsync() is { } err)
                        onStdError(err);
                }
                catch { /* 进程退出 */ }
            }, CancellationToken.None);
        }
        while (!ct.IsCancellationRequested)
        {
            IpcMessage? msg;
            try
            {
                msg = await ReceiveAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                break;
            }
            if (msg is null) break;
            onMessage(msg);
        }
    }

    public void Dispose()
    {
        try { _stdin.Dispose(); } catch { }
        try { _stdout.Dispose(); } catch { }
    }
}
