using DesktopManager.Core.Models;
using DesktopManager.Core.Services;
using Serilog;

namespace DesktopManager.App.Services;

/// <summary>M6 拆分：聚合持久化（从 MultiMonitorHost 抽出）。
/// 防抖保存 + 立即保存；聚合快照由宿主提供（Func&lt;AppConfig&gt;），本类不持有业务状态。</summary>
internal sealed class PersistenceService : IDisposable
{
    private readonly IConfigStore _store;
    private readonly Func<AppConfig> _buildAggregated;
    private readonly System.Threading.Timer _saveTimer;
    private readonly object _lock = new();
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private volatile bool _disabled;

    public PersistenceService(IConfigStore store, Func<AppConfig> buildAggregated)
    {
        _store = store;
        _buildAggregated = buildAggregated;
        _saveTimer = new System.Threading.Timer(_ => OnElapsed(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>布局/配置变更 → 防抖落盘。</summary>
    public void RequestSave()
    {
        if (_disabled) return;
        lock (_lock) _saveTimer.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>立即保存（不等防抖）。退出路径用。</summary>
    public void SaveNow()
    {
        _disabled = true;
        try { _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); } catch { }
        try
        {
            lock (_lock) _store.Save(_buildAggregated());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PersistenceService 立即保存失败");
        }
    }

    /// <summary>保存当前快照并返回（拓扑重建前用）。</summary>
    public AppConfig SaveAndReturn()
    {
        var snapshot = _buildAggregated();
        lock (_lock)
        {
            if (!_disabled) _store.Save(snapshot);
        }
        return snapshot;
    }

    private void OnElapsed()
    {
        if (_disabled) return;
        try
        {
            lock (_lock)
            {
                if (_disabled) return;
                _store.Save(_buildAggregated());
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PersistenceService 防抖保存失败");
        }
    }

    public void Dispose() => _saveTimer.Dispose();
}
