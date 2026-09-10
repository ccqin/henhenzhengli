using System.Runtime.InteropServices;
using Live2DCSharpSDK.App;
using Live2DCSharpSDK.Framework;
using Live2DCSharpSDK.OpenGL;
using Live2DCSharpSDK.OpenTK;
using Live2DCSharpSDK.Framework.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;

using OpenTK.Windowing.Desktop;

namespace DesktopManager.Plugin.Pet;

/// <summary>Live2D 离屏渲染宿主（Live2DPet 同款）：隐藏 GameWindow 上跑 Cubism 引擎，
/// 每帧 ReadPixels 读回带 Alpha 像素 → 转预乘 BGRA → FrameReady 抛出。
/// 不走 GLWpfControl/D3DImage（Intel A780 物理输出失效，真机验证）。
/// 必须在 UI 线程调用（OpenTK/GLEW 要求同线程）。</summary>
internal sealed class PetGlHost : IDisposable
{
    private readonly int _width, _height;
    private readonly string _modelDir, _modelName;
    private GameWindow? _window;
    private LAppDelegate? _lapp;
    private LAppModel? _model;
    private byte[] _readBuf = [];
    private byte[] _bgra = [];

    /// <summary>每帧 BGRA 预乘像素（供 PetLayeredWindow.PushFrame）。</summary>
    public event Action<byte[]>? FrameReady;

    public bool IsRunning => _window is not null && _model is not null;

    public PetGlHost(int width, int height, string modelDir, string modelName)
    {
        _width = width; _height = height;
        _modelDir = modelDir; _modelName = modelName;
    }

    public void Start()
    {
        var settings = new NativeWindowSettings
        {
            ClientSize = new Vector2i(_width, _height),
            Title = "PetGL",
            WindowBorder = WindowBorder.Hidden,
            StartVisible = false,
            StartFocused = false,
            Flags = ContextFlags.ForwardCompatible,
        };
        _window = new GameWindow(GameWindowSettings.Default, settings);
        GL.Viewport(0, 0, _width, _height);

        var allocator = new LAppAllocator();
        var option = new CubismOption { LogFunction = _ => { }, LoggingLevel = LAppDefine.CubismLoggingLevel };
        CubismFramework.StartUp(allocator, option);

        _lapp = new LAppDelegateOpenGL(new OpenTKApi(_window))
        {
            BGColor = new CubismTextureColor(0, 0, 0, 0),   // 透明清屏
        };
        _model = _lapp.Live2dManager.LoadModel(_modelDir, _modelName);
        _readBuf = new byte[_width * _height * 4];
        _bgra = new byte[_width * _height * 4];
    }

    /// <summary>每帧调用（UI 线程）：推引擎 → 读回 → 转换 → 抛帧。</summary>
    public unsafe void Tick(float dt)
    {
        if (_window is null || _lapp is null || _model is null) return;

        try
        {
            _lapp.Run(dt);

            fixed (byte* p = _readBuf)
                GL.ReadPixels(0, 0, _width, _height, PixelFormat.Rgba, PixelType.UnsignedByte, (IntPtr)p);

            ConvertToBgraPremultiplied(_readBuf, _width, _height, _bgra);
            FrameReady?.Invoke(_bgra);
            _window.ProcessEvents(0.0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[pet] GL tick fail: " + ex.Message);
        }
    }

    /// <summary>RGBA（自下而上）→ BGRA 预乘（自上而下，top-down DIB 格式）。</summary>
    private static void ConvertToBgraPremultiplied(byte[] src, int w, int h, byte[] dst)
    {
        int stride = w * 4;
        for (int y = 0; y < h; y++)
        {
            int srcRow = (h - 1 - y) * stride;   // 翻转垂直
            int dstRow = y * stride;
            for (int x = 0; x < w; x++)
            {
                int s = srcRow + x * 4, d = dstRow + x * 4;
                byte r = src[s], g = src[s + 1], b = src[s + 2], a = src[s + 3];
                dst[d] = (byte)(b * a / 255);
                dst[d + 1] = (byte)(g * a / 255);
                dst[d + 2] = (byte)(r * a / 255);
                dst[d + 3] = a;
            }
        }
    }

    public void Dispose()
    {
        _model?.Dispose();
        _lapp?.Dispose();
        _window?.Dispose();
    }
}
