using System.Runtime.InteropServices;
using Serilog;

namespace DesktopManager.Native;

/// <summary>显示器持久 ID 解析（M3-T1）。
/// <para>M0.5 已探明：GDI 设备名（<c>\\.\DISPLAYn</c>）随插拔/换排列顺序变化，不能做持久归属键。</para>
/// <para>首选方案本是 QueryDisplayConfig 的 TARGET 设备路径（EDID 硬件标识），但真机（Win11 24H2 +
/// Intel MTL iGPU）上 <c>DisplayConfigGetDeviceInfo(GET_TARGET_NAME)</c> 恒定 ERROR_INVALID_PARAMETER=87
/// （size/id/拓扑源扫描全部排除，疑似 DWM 虚拟显示模式的驱动层问题）。</para>
/// <para>落地方案（等价语义）：持久键 = <c>GET_ADAPTER_NAME 的 PCI 硬件路径 + "#" + source id</c>。
/// PCI 路径（如 <c>\\?\PCI#VEN_8086&amp;DEV_A780&amp;...</c>）是硬件标识、跨重启稳定；source id 是 GPU
/// 物理输出口索引，换排列顺序/重启不变（换物理接口会变，与设备路径语义一致）。</para>
/// <para>失败兜底：QueryDisplayConfig 不可用（RDP 会话/虚拟机/极老驱动）→ 返回空字典，
/// 调用方退化为 GDI 设备名（宁可串屏也不崩，见 MonitorEnumerator 兜底）。</para></summary>
public static class MonitorIdResolver
{
    // ---------- DisplayConfig P/Invoke ----------

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002; // wingdi.h: ALL_PATHS=1, ONLY_ACTIVE_PATHS=2（勿混淆）
    private const int ERROR_SUCCESS = 0;
    private const int CCHDEVICENAME = 32;
    private const int ADAPTER_PATH_MAX = 128;

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathInfoElements,
        [Out] DISPLAYCONFIG_PATH_INFO[]? pathInfoArray,
        ref uint numModeInfoElements,
        [Out] DISPLAYCONFIG_MODE_INFO[]? modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint numPathInfoElements, out uint numModeInfoElements);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_ADAPTER_NAME requestPacket);

    /// <summary>GDI 设备名（\\.\DISPLAYn，EnumDisplayMonitors 的 szDevice）→ 持久 ID
    /// （adapter PCI 路径 + source id）。失败/空拓扑返回空字典（调用方退化 GDI 名）。</summary>
    public static IReadOnlyDictionary<string, string> ResolveGdiNameToPersistentId()
    {
        try
        {
            var paths = QueryActivePaths();
            if (paths is null) return new Dictionary<string, string>();

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                string? gdi = GetSourceGdiName(path.sourceInfo.adapterId, path.sourceInfo.id);
                string? adapterPath = GetAdapterPath(path.sourceInfo.adapterId, path.sourceInfo.id);
                if (string.IsNullOrEmpty(gdi) || string.IsNullOrEmpty(adapterPath)) continue;
                // 持久键：PCI 硬件路径（跨重启稳定）+ GPU 输出口索引（换排列顺序不变）。
                map[gdi] = $"{adapterPath}#src{path.sourceInfo.id}";
            }
            return map;
        }
        catch (Exception ex)
        {
            // RDP/虚拟机/驱动异常：返回空字典，调用方退化 GDI 名。持久 ID 是增强项，不能反过来拖垮枚举。
            Log.Warning(ex, "MonitorIdResolver 异常，退化 GDI 设备名");
            return new Dictionary<string, string>();
        }
    }

    private static DISPLAYCONFIG_PATH_INFO[]? QueryActivePaths()
    {
        // 正规两段式：GetDisplayConfigBufferSizes 拿 path/mode 缓冲区大小，再全量 QueryDisplayConfig。
        // （零长探测 QueryDisplayConfig 返回 ERROR_INVALID_PARAMETER=87，真机已验证；活动路径查询要求同时提供 mode 缓冲区。）
        int hr = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
        if (hr != ERROR_SUCCESS || pathCount == 0)
        {
            Log.Warning("GetDisplayConfigBufferSizes 失败: hr={Hr} paths={P}", hr, pathCount);
            return null;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        hr = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        // 拓扑可能在两次调用间变化 → 返回数量可能变小，取实际 pathCount 段。
        return hr == ERROR_SUCCESS ? paths[..(int)pathCount] : null;
    }

    private static string? GetSourceGdiName(LUID adapterId, uint sourceId)
    {
        var req = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = { type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_SOURCE_NAME, size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), adapterId = adapterId, id = sourceId }
        };
        return DisplayConfigGetDeviceInfo(ref req) == ERROR_SUCCESS ? req.viewGdiDeviceName : null;
    }

    private static string? GetAdapterPath(LUID adapterId, uint sourceId)
    {
        var req = new DISPLAYCONFIG_ADAPTER_NAME
        {
            header = { type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_ADAPTER_NAME, size = (uint)Marshal.SizeOf<DISPLAYCONFIG_ADAPTER_NAME>(), adapterId = adapterId, id = sourceId }
        };
        return DisplayConfigGetDeviceInfo(ref req) == ERROR_SUCCESS ? req.adapterDevicePath : null;
    }

    // ---------- 结构体（与 wingdi.h 对齐；含字符串的结构体必须 CharSet.Unicode，否则 ANSI 编组 size 减半 → 87） ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    private enum DISPLAYCONFIG_DEVICE_INFO_TYPE : int
    {
        GET_SOURCE_NAME = 1,
        GET_ADAPTER_NAME = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public DISPLAYCONFIG_DEVICE_INFO_TYPE type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_ADAPTER_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADAPTER_PATH_MAX)]
        public string adapterDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_2DREGION { public uint cx; public uint cy; }

    /// <summary>wingdi.h 的 DISPLAYCONFIG_VIDEO_SIGNAL_INFO 是 union；Explicit 布局对齐，总长 48 字节。</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        [FieldOffset(0)] public ulong pixelRate;
        [FieldOffset(0)] public DISPLAYCONFIG_RATIONAL hSyncFreq;
        [FieldOffset(8)] public DISPLAYCONFIG_RATIONAL vSyncFreq;
        [FieldOffset(16)] public DISPLAYCONFIG_2DREGION activeSize;
        [FieldOffset(24)] public DISPLAYCONFIG_2DREGION totalSize;
        [FieldOffset(32)] public uint videoStandard;
        [FieldOffset(36)] public uint scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public int outputTechnology;      // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY
        public int rotation;              // DISPLAYCONFIG_ROTATION
        public int scaling;               // DISPLAYCONFIG_SCALING
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public int scanLineOrdering;      // DISPLAYCONFIG_SCANLINE_ORDERING
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>DISPLAYCONFIG_MODE_INFO（union，总长 64 字节）。本类只需缓冲区尺寸正确，不解读内容，
    /// union 部分用 48 字节占位（target mode 最大成员尺寸）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public int infoType;
        public uint id;
        public LUID adapterId;
        public ModeInfoUnion union;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct ModeInfoUnion { }
}
