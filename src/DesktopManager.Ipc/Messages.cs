using System.Text.Json.Serialization;

namespace DesktopManager.Ipc;

/// <summary>IPC 消息基类。Type 属性做多态反序列化分发。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Ready), "ready")]
[JsonDerivedType(typeof(LayoutChanged), "layoutChanged")]
[JsonDerivedType(typeof(IconOpened), "iconOpened")]
[JsonDerivedType(typeof(Error), "error")]
[JsonDerivedType(typeof(TransferLooseReq), "transferLooseReq")]
[JsonDerivedType(typeof(TransferFenceReq), "transferFenceReq")]
[JsonDerivedType(typeof(ExportIconData), "exportIconData")]
[JsonDerivedType(typeof(ExportFenceData), "exportFenceData")]
[JsonDerivedType(typeof(ClearSelectionExcept), "clearSelectionExcept")]
[JsonDerivedType(typeof(FenceAction), "fenceAction")]
[JsonDerivedType(typeof(IconAction), "iconAction")]
[JsonDerivedType(typeof(SetWallpaper), "setWallpaper")]
[JsonDerivedType(typeof(SetIcons), "setIcons")]
[JsonDerivedType(typeof(ApplyDiff), "applyDiff")]
[JsonDerivedType(typeof(SetFences), "setFences")]
[JsonDerivedType(typeof(Pause), "pause")]
[JsonDerivedType(typeof(Resume), "resume")]
[JsonDerivedType(typeof(Show), "show")]
[JsonDerivedType(typeof(SetPosition), "setPosition")]
[JsonDerivedType(typeof(Shutdown), "shutdown")]
[JsonDerivedType(typeof(ExportIcon), "exportIcon")]
[JsonDerivedType(typeof(ImportIcon), "importIcon")]
[JsonDerivedType(typeof(ExportFence), "exportFence")]
[JsonDerivedType(typeof(ImportFence), "importFence")]
[JsonDerivedType(typeof(MoveFencePos), "moveFencePos")]
[JsonDerivedType(typeof(ClearSelection), "clearSelection")]
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

/// <summary>图标层：跨屏图标迁移请求（目标窗查无归属 → 请主进程找源屏导出）。</summary>
public sealed record TransferLooseReq : IpcMessage
{
    public string Path { get; init; } = "";
    public string TargetMonitorId { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
}

/// <summary>图标层：Fence 跨屏迁移请求。</summary>
public sealed record TransferFenceReq : IpcMessage
{
    public string FenceId { get; init; } = "";
    public string TargetMonitorId { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
}

/// <summary>图标层：ExportIcon 的应答（源窗导出结果）。</summary>
public sealed record ExportIconData : IpcMessage
{
    public bool Found { get; init; }
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
}

/// <summary>图标层：ExportFence 的应答。</summary>
public sealed record ExportFenceData : IpcMessage
{
    public bool Found { get; init; }
    public FenceDto? Fence { get; init; }
}

/// <summary>图标层：本屏将选中某图标 → 请求清除其余屏选中态。</summary>
public sealed record ClearSelectionExcept : IpcMessage
{
    public string MonitorId { get; init; } = "";
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

/// <summary>图标层操作审计上报（建/删/改名/折叠/移动收纳盒等用户操作，主进程落库）。</summary>
public sealed record FenceAction : IpcMessage
{
    public string Action { get; init; } = "";   // create/delete/rename/fold/move
    public string FenceId { get; init; } = "";
    public string Title { get; init; } = "";
}

/// <summary>图标操作审计上报（打开/重命名/删除文件等）。</summary>
public sealed record IconAction : IpcMessage
{
    public string Action { get; init; } = "";   // open/rename/delete/locate/move
    public string Path { get; init; } = "";
    public string Detail { get; init; } = "";
}

// ---- 主进程 → 图标层子进程（跨屏迁移编排） ----

/// <summary>源窗导出图标（主进程中转跨屏迁移）。</summary>
public sealed record ExportIcon : IpcMessage
{
    public string Path { get; init; } = "";
}

/// <summary>目标窗导入图标（落到指定坐标）。</summary>
public sealed record ImportIcon : IpcMessage
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
}

/// <summary>源窗导出 Fence。</summary>
public sealed record ExportFence : IpcMessage
{
    public string FenceId { get; init; } = "";
}

/// <summary>目标窗导入 Fence（X/Y = Drop 位置）。</summary>
public sealed record ImportFence : IpcMessage
{
    public FenceDto Fence { get; init; } = new();
    public double X { get; init; }
    public double Y { get; init; }
}

/// <summary>同窗 Fence 拖动换位置。</summary>
public sealed record MoveFencePos : IpcMessage
{
    public string FenceId { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
}

/// <summary>清除本屏选中态（跨屏单选广播，发往除请求屏外的所有图标层）。</summary>
public sealed record ClearSelection : IpcMessage;

// ---- 共享 DTO ----

public sealed record FenceDto
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }
    public bool Collapsed { get; init; }
    public List<string> IconPaths { get; init; } = [];
}

public sealed record IconDto
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public string? FenceId { get; init; }
}

public sealed record IconPosDto
{
    public string Path { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public string? FenceId { get; init; }
}
