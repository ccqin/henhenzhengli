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

    /// <summary>立即保存（保持后续防抖能力）。关键低频操作（建盒/改名/删盒/壁纸/设置类）专用——
    /// 防抖窗口内进程被杀会丢最近改动（真机：收纳盒数据丢失）；高频操作（拖图标）仍走防抖。</summary>
    public void SaveImmediately()
    {
        if (_disabled) return;
        SaveCore("immediate");
    }

    /// <summary>立即保存（不等防抖）。退出路径用。</summary>
    public void SaveNow()
    {
        _disabled = true;
        try { _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); } catch { }
        SaveCore("final");
    }

    /// <summary>统一保存核心：聚合 + 诊断日志 + 僵尸写防护。
    /// 真机教训（2026-08-27）：一次退出保存把 145 个图标位置+收纳盒写成空——聚合来源为空时
    /// 若磁盘上已有数据，宁可不写也不清空（桌面图标永在，Positions 为空只可能是异常态）。</summary>
    private void SaveCore(string reason)
    {
        try
        {
            AppConfig snapshot;
            lock (_lock) snapshot = _buildAggregated();
            if (snapshot.IconPositions.Count == 0)
            {
                var onDisk = _store.Load();
                if (onDisk.IconPositions.Count > 0)
                {
                    Log.Warning("跳过空快照保存（{Reason}）：聚合 0 位置，磁盘 {N} 条——防僵尸实例清空配置",
                        reason, onDisk.IconPositions.Count);
                    return;
                }
            }
            lock (_lock)
            {
                _store.Save(snapshot);
            }
            Log.Debug("config 保存（{Reason}）：{F} 收纳盒 / {P} 位置", reason, snapshot.Fences.Count, snapshot.IconPositions.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PersistenceService 保存失败（{Reason}）", reason);
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
        SaveCore("debounce");
    }

    public void Dispose() => _saveTimer.Dispose();
}
