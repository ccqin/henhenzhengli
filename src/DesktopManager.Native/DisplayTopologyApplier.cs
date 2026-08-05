using System.Runtime.InteropServices;
using System.Text;

namespace DesktopManager.Native;

/// <summary>M5-T5（M3-T8 欠账）：把新排列应用到 Windows 真实显示拓扑。
/// 主路径 = CCD（Windows 设置同款）：QueryDisplayConfig 拿 paths+modes → 改各 path 的
/// source mode position → <c>SetDisplayConfig(SDC_APPLY|SDC_USE_SUPPLIED_DISPLAY_CONFIG|...)</c> 一次性生效。
/// 真机教训：本机（Intel MTL，DWM 虚拟显示模式）legacy ChangeDisplaySettingsEx 全坏
/// （EnumDisplaySettings 读不到当前位置、apply 返回 -5/-1），同 GET_TARGET_NAME=87 一族。
/// 成功后 WM_DISPLAYCHANGE 自动触发 M3 重建链路。只改位置，不改分辨率/主屏。legacy 路径保留作降级。</summary>
public static class DisplayTopologyApplier
{
    // ---------- DisplayConfig P/Invoke ----------
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int ERROR_SUCCESS = 0;

    private const uint SDC_TOPOLOGY_SUPPLIED = 0x00000010;   // = SDC_USE_SUPPLIED_DISPLAY_CONFIG
    private const uint SDC_VALIDATE = 0x00000040;
    private const uint SDC_APPLY = 0x00000080;
    private const uint SDC_NO_OPTIMIZATION = 0x00000100;
    private const uint SDC_SAVE_TO_DATABASE = 0x00000200;

    private const int DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathInfoElements, out uint numModeInfoElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags, ref uint numPathInfoElements, [In, Out] DISPLAYCONFIG_PATH_INFO[]? pathInfoArray,
        ref uint numModeInfoElements, [In, Out] DISPLAYCONFIG_MODE_INFO[]? modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern long SetDisplayConfig(
        uint numPathArrayElements, [In, Out] DISPLAYCONFIG_PATH_INFO[]? pathArray,
        uint numModeInfoArrayElements, [In, Out] DISPLAYCONFIG_MODE_INFO[]? modeArray, uint flags);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    /// <param name="positions">GDI 设备名（\\.\DISPLAYn）→ 新左上角坐标。</param>
    public static (bool Ok, string Error) Apply(IReadOnlyDictionary<string, (int X, int Y, int W, int H)> positions)
    {
        // 真机：legacy 带显式分辨率可过（CCD validate=87 未解）；legacy 主路径，CCD 降级。
        var legacy = ApplyViaLegacyCds(positions);
        if (legacy.Ok) return legacy;

        var ccd = ApplyViaDisplayConfig(positions);
        if (ccd.Ok) return ccd;
        return (false, $"legacy: {legacy.Error}；CCD: {ccd.Error}");
    }

    // ---------- 主路径：CCD ----------

    private static (bool Ok, string Error) ApplyViaDisplayConfig(IReadOnlyDictionary<string, (int X, int Y, int W, int H)> positions)
    {
        try
        {
            int hr = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
            if (hr != ERROR_SUCCESS || pathCount == 0) return (false, $"GetBufferSizes hr={hr}");

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            uint pc = pathCount, mc = modeCount;
            hr = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pc, paths, ref mc, modes, IntPtr.Zero);
            if (hr != ERROR_SUCCESS) return (false, $"QueryDisplayConfig hr={hr}");

            int changed = 0;
            for (int i = 0; i < pc; i++)
            {
                var gdi = GetSourceGdiName(paths[i].sourceInfo.adapterId, paths[i].sourceInfo.id);
                if (gdi is null || !positions.TryGetValue(gdi, out var pos)) continue;
                int mi = (int)paths[i].sourceInfo.modeInfoIdx;
                if (mi < 0 || mi >= mc || modes[mi].infoType != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE) continue;
                modes[mi].u.sourcePosition.cx = (uint)pos.X;
                modes[mi].u.sourcePosition.cy = (uint)pos.Y;
                changed++;
            }
            if (changed == 0) return (false, "无匹配的 path（GDI 名对不上）");

            // 先 VALIDATE 再 APPLY，失败不污染现状
            long rv = SetDisplayConfig(pc, paths, mc, modes,
                SDC_TOPOLOGY_SUPPLIED | SDC_VALIDATE | SDC_NO_OPTIMIZATION);
            if (rv != ERROR_SUCCESS) return (false, $"SetDisplayConfig validate={rv}");
            rv = SetDisplayConfig(pc, paths, mc, modes,
                SDC_TOPOLOGY_SUPPLIED | SDC_APPLY | SDC_NO_OPTIMIZATION | SDC_SAVE_TO_DATABASE);
            return rv == ERROR_SUCCESS
                ? (true, "")
                : (false, $"SetDisplayConfig apply={rv}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string? GetSourceGdiName(LUID adapterId, uint sourceId)
    {
        var req = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header =
            {
                type = 1, // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = adapterId,
                id = sourceId
            }
        };
        return DisplayConfigGetDeviceInfo(ref req) == ERROR_SUCCESS ? req.viewGdiDeviceName : null;
    }

    // ---------- 降级路径：legacy CDS ----------

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DM_POSITION = 0x00000020;
    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const uint CDS_NORESET = 0x10000000;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOQuality;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int ChangeDisplaySettingsEx(
        string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int ChangeDisplaySettingsEx(
        string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    private const int DM_DISPLAYFREQUENCY = 0x00400000;
    private const int DM_PELSWIDTH = 0x00080000;
    private const int DM_PELSHEIGHT = 0x00100000;
    private const int DM_BITSPERPEL = 0x00040000;

    private static (bool Ok, string Error) ApplyViaLegacyCds(IReadOnlyDictionary<string, (int X, int Y, int W, int H)> positions)
    {
        // 真机教训（Intel MTL DWM 虚拟模式）：ENUM_CURRENT_SETTINGS 读得到 position 但 pels=0，
        // 只传 DM_POSITION 会让驱动按 pels=0 校验整模式 → -5/-1。必须显式带当前分辨率+色深。
        foreach (var (name, (x, y, w, h)) in positions)
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(name, ENUM_CURRENT_SETTINGS, ref dm))
                return (false, $"EnumDisplaySettings 失败：{name}");
            dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL;
            dm.dmPositionX = x;
            dm.dmPositionY = y;
            dm.dmPelsWidth = w;
            dm.dmPelsHeight = h;
            dm.dmBitsPerPel = 32;
            int r = ChangeDisplaySettingsEx(name, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            if (r != DISP_CHANGE_SUCCESSFUL)
                return (false, $"{name} code={r}");
        }
        int final = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        return final == DISP_CHANGE_SUCCESSFUL ? (true, "") : (false, $"final code={final}");
    }

    // ---------- 诊断（--debug-monitors 用） ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    /// <summary>诊断：枚举 GDI 设备 + 当前位置读取 + CCD validate 探针（真机排障用）。</summary>
    public static string Diagnose()
    {
        var sb = new StringBuilder();
        for (uint i = 0; i < 8; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            bool ok = EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm);
            sb.Append($"dev[{i}] {dd.DeviceName} flags=0x{dd.StateFlags:X} enumOk={ok} pos=({dm.dmPositionX},{dm.dmPositionY}) res={dm.dmPelsWidth}x{dm.dmPelsHeight} | ");
        }
        return sb.ToString();
    }

    // ---------- 结构体 ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_2DREGION { public uint cx; public uint cy; }

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
        public int outputTechnology;
        public int rotation;
        public int scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public int scanLineOrdering;
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

    /// <summary>DISPLAYCONFIG_MODE_INFO（64 字节）。union 暴露 source mode 字段（position/size），
    /// 其余区域（target mode 等）作填充——CCD 只改 source position，原样回传。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public int infoType;
        public uint id;
        public LUID adapterId;
        public ModeInfoUnion u;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct ModeInfoUnion
    {
        // 真机字节解码（hex dump）：source mode 实际布局 = size@0, pixelFormat@8, position@12
        // （与 SDK 文档顺序不同，本机 Win11 24H2 实测；写错偏移 → SetDisplayConfig 恒 87）。
        [FieldOffset(0)] public DISPLAYCONFIG_2DREGION sourceSize;
        [FieldOffset(8)] public int pixelFormat;
        [FieldOffset(12)] public DISPLAYCONFIG_2DREGION sourcePosition;
    }
}
