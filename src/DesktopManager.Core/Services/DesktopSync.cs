using System.IO;
using DesktopManager.Core.Models;
using Serilog;
namespace DesktopManager.Core.Services;

/// <summary>监听桌面文件变化：FileSystemWatcher 事件 + 定时全量对账双保险（FSW 漏事件时对账兜底）。</summary>
public sealed class DesktopSync : IDisposable
{
    private readonly IDesktopSnapshot _snapshot;
    private readonly FileSystemWatcher[] _watchers;
    private readonly Timer _reconcileTimer;
    private IReadOnlyList<IconItem> _current;
    private readonly object _lock = new();

    public event EventHandler<DesktopDiff>? Changed;

    public DesktopSync(IDesktopSnapshot snapshot, IEnumerable<string> folders, TimeSpan reconcileInterval)
    {
        _snapshot = snapshot;
        _current = snapshot.Capture();
        _watchers = folders.Where(Directory.Exists).Select(f =>
        {
            var w = new FileSystemWatcher(f) { IncludeSubdirectories = false, EnableRaisingEvents = true };
            w.Created += (_, _) => Reconcile();
            w.Deleted += (_, _) => Reconcile();
            w.Renamed += (_, _) => Reconcile();
            return w;
        }).ToArray();
        _reconcileTimer = new Timer(_ => Reconcile(), null, reconcileInterval, reconcileInterval);
    }

    public IReadOnlyList<IconItem> Current { get { lock (_lock) return _current; } }

    private void Reconcile()
    {
        // 整体 try/catch：防 Capture/Diff 抛异常（IO 瞬时失败/权限丢失）→ FSW 回调停止 / Timer 停转（I-4）。
        // 不重抛：watcher 与 Timer 继续工作，单次失败不致 sync 静默失活。
        try
        {
            DesktopDiff? diff = null;
            lock (_lock)
            {
                var latest = _snapshot.Capture();
                diff = DesktopDiff.Diff(_current, latest);
                if (diff.Added.Count == 0 && diff.Removed.Count == 0) return;
                _current = latest;
            }
            Changed?.Invoke(this, diff);
        }
        catch (System.Exception ex)
        {
            // 可恢复降级：单次对账失败不致 sync 失活（watcher/Timer 继续），但需记录便于诊断 IO 瞬时问题。
            Log.Warning(ex, "DesktopSync.Reconcile 失败");
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _reconcileTimer.Dispose();
    }
}
