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

    /// <summary>读取一行并反序列化；流结束返回 null。空行跳过。</summary>
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
            return JsonSerializer.Deserialize<IpcMessage>(line, Options)
                ?? throw new JsonException("Deserialized to null");
        }
    }
}
