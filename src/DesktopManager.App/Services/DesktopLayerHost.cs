using DesktopManager.Native;
using Serilog;

namespace DesktopManager.App.Services;

/// <summary>M6：桌面层挂载编排。WorkerW 只找一次并缓存（0x052C 全局只发一次即可），
/// 子进程窗口 Ready 后 SetParent 到 WorkerW 成为桌面子窗口（免疫 Win+D）。</summary>
public static class DesktopLayerHost
{
    private static IntPtr _workerW;
    private static readonly object Lock = new();

    /// <summary>获取 WorkerW（懒初始化，线程安全）。失败返回 IntPtr.Zero。</summary>
    public static IntPtr GetWorkerW()
    {
        lock (Lock)
        {
            if (_workerW != IntPtr.Zero) return _workerW;
            _workerW = WindowInterop.SetupDesktopLayer();
            if (_workerW == IntPtr.Zero)
                Log.Error("SetupDesktopLayer 失败：找不到 Progman/WorkerW");
            return _workerW;
        }
    }

    /// <summary>WorkerW 缓存失效（explorer 重启后必须重找）。</summary>
    public static void Invalidate() { lock (Lock) _workerW = IntPtr.Zero; }

    /// <summary>把子进程窗口挂到桌面层。iconLayer=true 用色键透明（见 WindowInterop.AttachToDesktop 注释）。</summary>
    public static void AttachToDesktop(long childHwnd, int monX, int monY, int monW, int monH, bool iconLayer = false)
    {
        var workerW = GetWorkerW();
        if (workerW == IntPtr.Zero) throw new InvalidOperationException("WorkerW 不可用");
        WindowInterop.AttachToDesktop(new IntPtr(childHwnd), workerW, monX, monY, monW, monH, iconLayer);
    }
}
