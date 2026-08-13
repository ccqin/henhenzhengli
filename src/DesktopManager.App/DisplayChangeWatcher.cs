using DesktopManager.Native;
using System.Windows;
using System.Windows.Interop;
using Serilog;

namespace DesktopManager.App;

/// <summary>M3-T6：显示器拓扑变化监听（热插拔/分辨率/DPI/主屏切换）。
/// 隐藏**顶层**窗口收 <c>WM_DISPLAYCHANGE</c>（拓扑切换过程会连发多条 → 500ms 防抖），
/// 稳定后回 UI 线程发 <see cref="DisplayChanged"/>，host 据此重建窗口集。
/// <para>真机踩坑：message-only 窗口（HWND_MESSAGE 子窗）收不到 HWND_BROADCAST 广播 →
/// 重建从不触发（拔线窗口不关、插回不建窗）。必须用顶层窗口（ParentWindow=0），隐藏且放屏外。</para>
/// <para>用独立窗口而非挂某个图标层窗口：重建过程窗口会关/建，监听器不能跟着死。</para></summary>
public sealed class DisplayChangeWatcher : IDisposable
{
    private const int WM_DISPLAYCHANGE = 0x007E;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private HwndSource? _source;
    private System.Threading.Timer? _debounce;

    /// <summary>拓扑稳定（防抖后），UI 线程触发。</summary>
    public event Action? DisplayChanged;

    public void Attach()
    {
        // 顶层窗口（ParentWindow 缺省 0）才能收广播；1x1 放屏外，不可见不抢交互。
        var p = new HwndSourceParameters("DisplayChangeWatcher", 1, 1);
        p.SetPosition(-32000, -32000);
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        
        // 隐藏窗口：不在任务栏显示 + 不在 Alt+Tab 显示
        var hwnd = _source.Handle;
        var ex = WindowInterop.GetExtendedStyle(hwnd);
        WindowInterop.SetExtendedStyle(hwnd, ex | 0x00000080); // WS_EX_TOOLWINDOW
        _debounce = new System.Threading.Timer(_ =>
        {
            // Timer 在 ThreadPool 触发 → 回 UI 线程（host 重建窗口必须 UI 线程）。
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => DisplayChanged?.Invoke()));
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Log.Debug("DisplayChangeWatcher 已挂载（message-only hwnd）");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DISPLAYCHANGE)
        {
            // 连发防抖：每次重置 500ms 一次性计时。
            try { _debounce?.Change(Debounce, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* 释放竞态，忽略 */ }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _debounce?.Dispose();
        _debounce = null;
        _source?.Dispose();
        _source = null;
    }
}
