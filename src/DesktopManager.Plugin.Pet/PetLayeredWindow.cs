using System.Runtime.InteropServices;

namespace DesktopManager.Plugin.Pet;

/// <summary>Win32 分层窗口（WS_EX_LAYERED + UpdateLayeredWindow per-pixel alpha）。
/// Live2DPet 同款方案：完全绕过 WPF 合成器/D3DImage（Intel A780 物理输出失效，真机验证），
/// GDI 直接合成到桌面。每帧 PushFrame(BGRA 预乘像素) 更新画面。
/// 坐标/交互由 PetBrain 驱动，窗口位置跟随猫的 X/Y（MoveTo）。</summary>
internal sealed class PetLayeredWindow : IDisposable
{
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const byte AC_SRC_OVER = 0;
    private const byte AC_SRC_PREMULT = 1;
    private const uint ULW_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint biColors; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string name, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dcDst, ref POINT dst, ref SIZE size, IntPtr dcSrc, ref POINT src, int crKey, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);

    private IntPtr _hwnd;
    private IntPtr _hdcMem;
    private IntPtr _hBitmap;
    private IntPtr _ppvBits;
    private int _w, _h;
    private bool _disposed;

    /// <summary>创建指定尺寸的分层窗口（初始隐藏，首次 PushFrame 后显示）。</summary>
    public void Create(int x, int y, int width, int height)
    {
        _w = width; _h = height;
        var inst = GetModuleHandleW(null);
        // Static 控件当壳（不需要消息循环，WinForms 消息泵已在跑）
        _hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "Static", "", WS_POPUP,
            x, y, width, height,
            IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);

        var hdc = GetDC(_hwnd);
        _hdcMem = CreateCompatibleDC(hdc);
        ReleaseDC(_hwnd, hdc);

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,   // top-down DIB（省去垂直翻转）
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,    // BI_RGB
            },
        };
        _hBitmap = CreateDIBSection(_hdcMem, ref bmi, 0 /* DIB_RGB_COLORS */, out _ppvBits, IntPtr.Zero, 0);
        SelectObject(_hdcMem, _hBitmap);
    }

    /// <summary>推帧：BGRA 预乘像素 → DIB → UpdateLayeredWindow。</summary>
    public unsafe void PushFrame(byte[] bgraPremultiplied)
    {
        if (_disposed || _hwnd == IntPtr.Zero || _ppvBits == IntPtr.Zero) return;
        if (bgraPremultiplied.Length < _w * _h * 4) return;

        fixed (byte* src = bgraPremultiplied)
        {
            Buffer.MemoryCopy(src, (void*)_ppvBits, _w * _h * 4, _w * _h * 4);
        }

        var ppt = new POINT();  // 0,0 = 不改变位置
        var psize = new SIZE { cx = _w, cy = _h };
        var psrc = new POINT();
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_PREMULT,
        };
        UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref ppt, ref psize, _hdcMem, ref psrc, 0, ref blend, ULW_ALPHA);
    }

    /// <summary>移动窗口（猫走动时跟随）。</summary>
    public void MoveTo(int x, int y)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010, SWP_NOZORDER = 0x0004;
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
    }

    public void Show() { if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, 5 /*SW_SHOW*/); }
    public void Hide() { if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, 0 /*SW_HIDE*/); }

    /// <summary>设置点击穿透（拖拽结束后恢复 false = 可交互）。</summary>
    public void SetClickThrough(bool on)
    {
        // 通过 ShowWindow(SW_HIDE)+重创建太重；直接改 ex style 需 SetWindowLong（此壳未引入——保持 false）
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        if (_hdcMem != IntPtr.Zero) { DeleteDC(_hdcMem); _hdcMem = IntPtr.Zero; }
        if (_hBitmap != IntPtr.Zero) { DeleteObject(_hBitmap); _hBitmap = IntPtr.Zero; }
    }
}
