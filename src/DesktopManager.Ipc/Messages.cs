using System.Text.Json.Serialization;

namespace DesktopManager.Ipc;

/// <summary>IPC 消息基类。Type 属性做多态反序列化分发。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Ready), "ready")]
[JsonDerivedType(typeof(LayoutChanged), "layoutChanged")]
[JsonDerivedType(typeof(IconOpened), "iconOpened")]
[JsonDerivedType(typeof(Error), "error")]
[JsonDerivedType(typeof(SetWallpaper), "setWallpaper")]
[JsonDerivedType(typeof(SetIcons), "setIcons")]
[JsonDerivedType(typeof(ApplyDiff), "applyDiff")]
[JsonDerivedType(typeof(SetFences), "setFences")]
[JsonDerivedType(typeof(Pause), "pause")]
[JsonDerivedType(typeof(Resume), "resume")]
[JsonDerivedType(typeof(Show), "show")]
[JsonDerivedType(typeof(SetPosition), "setPosition")]
[JsonDerivedType(typeof(Shutdown), "shutdown")]
public abstract record IpcMessage
{
    /// <summary>协议版本。</summary>
    public int V { get; init; } = 1;
}

// ---- 子进程 → 主进程 ----

/// <summary>子进程窗口就绪，上报 hwnd。</summary>
public sealed record Ready : IpcMessage
{
    public long Hwnd { get; init; }
}

/// <summary>图标层布局变更（拖拽/建删 Fence 后上报主进程持久化）。</summary>
public sealed record LayoutChanged : IpcMessage
{
    public List<FenceDto> Fences { get; init; } = [];
    public List<IconPosDto> Positions { get; init; } = [];
}

/// <summary>图标被用户打开。</summary>
public sealed record IconOpened : IpcMessage
{
    public string Path { get; init; } = "";
}

/// <summary>子进程内部错误上报。</summary>
public sealed record Error : IpcMessage
{
    public string Message { get; init; } = "";
}

// ---- 主进程 → 子进程 ----

/// <summary>设置壁纸（静态图/GIF/视频），可带跨屏裁剪 rect。</summary>
public sealed record SetWallpaper : IpcMessage
{
    public string Path { get; init; } = "";
    public string Kind { get; init; } = "image";
    public int? CropX { get; init; }
    public int? CropY { get; init; }
    public int? CropW { get; init; }
    public int? CropH { get; init; }
    public int CanvasW { get; init; }
    public int CanvasH { get; init; }
}

/// <summary>图标全量初始化。</summary>
public sealed record SetIcons : IpcMessage
{
    public List<IconDto> Items { get; init; } = [];
}

/// <summary>图标增量同步。</summary>
public sealed record ApplyDiff : IpcMessage
{
    public List<IconDto> Added { get; init; } = [];
    public List<string> Removed { get; init; } = [];
}

/// <summary>Fence 全量下发。</summary>
public sealed record SetFences : IpcMessage
{
    public List<FenceDto> Fences { get; init; } = [];
}

/// <summary>暂停播放。</summary>
public sealed record Pause : IpcMessage;

/// <summary>恢复播放。</summary>
public sealed record Resume : IpcMessage;

/// <summary>显示窗口（SetParent 完成后由主进程下发）。</summary>
public sealed record Show : IpcMessage;

/// <summary>更新窗口位置/尺寸（拓扑变化）。</summary>
public sealed record SetPosition : IpcMessage
{
    public int X { get; init; }
    public int Y { get; init; }
    public int W { get; init; }
    public int H { get; init; }
}

/// <summary>正常退出指令。</summary>
public sealed record Shutdown : IpcMessage;

// ---- 共享 DTO ----

public sealed record FenceDto
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public int X { get; init; }
    public int Y { get; init; }
    public int W { get; init; }
    public int H { get; init; }
    public bool Collapsed { get; init; }
}

public sealed record IconDto
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public int X { get; init; }
    public int Y { get; init; }
    public string? FenceId { get; init; }
}

public sealed record IconPosDto
{
    public string Path { get; init; } = "";
    public int X { get; init; }
    public int Y { get; init; }
    public string? FenceId { get; init; }
}
