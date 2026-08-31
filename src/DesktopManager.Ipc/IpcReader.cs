using System.IO;
using System.Text.Json;

namespace DesktopManager.Ipc;

/// <summary>按 JSON 行读取 IPC 消息。</summary>
public static class IpcReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>从流读取。注意：调用方必须复用同一个 StreamReader（内部有缓冲）。</summary>
    public static StreamReader OpenReader(Stream stream) =>
        new(stream, leaveOpen: true);

    /// <summary>读取一行并反序列化；流结束返回 null。空行跳过。
    /// 坏行（stdout 被日志等污染）跳过而非抛出——通道读循环一旦抛出整条 IPC 失联
    /// （真机：图标层日志混入 stdout → JsonException → 两屏通道全死 → 桌面假死黑屏）。</summary>
    public static async Task<IpcMessage?> ReadAsync(TextReader reader, CancellationToken ct = default)
    {
        while (true)
        {
#if NET8_0_OR_GREATER
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
#else
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
#endif
            if (line is null) return null;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                return JsonSerializer.Deserialize<IpcMessage>(line, Options);
            }
            catch (JsonException)
            {
                continue; // 非 IPC 行（子进程 stdout 污染）：丢弃，保通道存活
            }
        }
    }
}
