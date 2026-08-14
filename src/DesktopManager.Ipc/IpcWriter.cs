using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopManager.Ipc;

/// <summary>向 Stream 按 JSON 行写出 IPC 消息。</summary>
public static class IpcWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write(Stream stream, IpcMessage message)
    {
        var json = JsonSerializer.Serialize<IpcMessage>(message, Options);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        stream.Write(bytes);
        stream.Flush();
    }
}
