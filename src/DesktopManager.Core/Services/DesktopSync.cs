using System.IO;
using DesktopManager.Core.Models;
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

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _reconcileTimer.Dispose();
    }
}
