using System.IO;
using DesktopManager.Core.Services;
using DesktopManager.Ipc;
using DesktopManager.Native;
using System.Windows;
using Serilog;

namespace DesktopManager.App.Services;

/// <summary>一个已加载插件的运行时状态。</summary>
internal sealed class PluginRuntime
{
    public required PluginManifest Manifest { get; init; }
    public required ChildProcessManager Player { get; init; }
    public required bool BuiltIn { get; init; }
    public bool ReceivedHello { get; set; }   // ready 后是否完成 PluginHello 握手（校验清单一致）
}

/// <summary>M8 插件宿主：扫描清单 → 生命周期 → 桌面层挂载/Z 序 → 配置代存。
/// 插件与壁纸/图标层同构（独立 exe + JSON 行 IPC + ready{hwnd}），复用 ChildProcessManager。
/// 崩溃安全：插件异常退出不自动重启（日志 + 设置页标红，用户手动再启，防坏插件风暴重启）。</summary>
internal sealed class PluginManager : IDisposable
{
    private readonly Func<PluginConfigState> _getConfig;
    private readonly Action _saveNow;
    private readonly Dictionary<string, PluginRuntime> _running = new(StringComparer.Ordinal);
    private readonly List<PluginManifest> _discovered = [];

    public PluginManager(Func<PluginConfigState> getConfig, Action saveNow)
    {
        _getConfig = getConfig;
        _saveNow = saveNow;
    }

    public IReadOnlyList<PluginManifest> Discovered => _discovered;
    public IReadOnlyDictionary<string, PluginRuntime> Running => _running;

    /// <summary>插件目录：内置（包内，只读）+ 用户（%APPDATA%，第三方可写）。同 id 内置优先（防伪造官方）。</summary>
    public static IEnumerable<string> PluginRoots() =>
    [
        Path.Combine(AppContext.BaseDirectory, "plugins"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopManager", "plugins"),
    ];

    /// <summary>扫描全部插件目录，解析清单（坏清单跳过并记日志）。</summary>
    public void Discover()
    {
        _discovered.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in PluginRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var manifestPath = Path.Combine(dir, "plugin.json");
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    var m = PluginManifest.TryParse(File.ReadAllText(manifestPath), dir);
                    if (m is null)
                    {
                        Log.Warning("插件清单无效，跳过：{Path}", manifestPath);
                        continue;
                    }
                    if (!seen.Add(m.Id))
                    {
                        Log.Information("插件 {Id} 重复（内置优先），跳过：{Dir}", m.Id, dir);
                        continue;
                    }
                    _discovered.Add(m);
                    Log.Information("发现插件：{Id} {Version}（{Dir}）", m.Id, m.Version, dir);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "插件目录扫描失败：{Dir}", dir);
                }
            }
        }
    }

    /// <summary>启动 config 中启用的插件（应用启动 / 设置页开启单个）。</summary>
    public void StartEnabled()
    {
        var enabled = _getConfig().Enabled;
        foreach (var id in enabled)
        {
            var m = _discovered.FirstOrDefault(x => x.Id == id);
            if (m is null)
            {
                Log.Warning("已启用但未发现插件：{Id}（清单缺失或无效）", id);
                continue;
            }
            Start(m);
        }
    }

    public void Start(PluginManifest m)
    {
        if (_running.ContainsKey(m.Id)) return;
        if (!File.Exists(m.EntryPath))
        {
            Log.Warning("插件入口不存在：{Entry}", m.EntryPath);
            return;
        }
        try
        {
            var player = new ChildProcessManager(m.Id);
            player.MessageReceived += msg => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnMessage(m.Id, msg));
            player.Exited += code =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    _running.Remove(m.Id);
                    if (code != 0) Log.Warning("插件 {Id} 异常退出（code={Code}），可在设置中重新启用", m.Id, code);
                });
            };
            var hwnd = player.StartAsync(m.EntryPath, "").GetAwaiter().GetResult();
            // 挂桌面层：可交互插件（宠物/小组件）iconLayer:true（不穿透）；特效类穿透
            // 全虚拟桌面尺寸（插件可跨屏走动；宿主不限定工作区）
            var vs = SystemParameters.VirtualScreenLeft;
            var vsTop = SystemParameters.VirtualScreenTop;
            // 底部 2px 缝（壁纸同款真机教训）：顶层全屏无边框窗触发 shell 全屏检测 → 任务栏自动隐藏
            DesktopLayerHost.AttachToDesktop(hwnd, (int)vs, (int)vsTop,
                (int)SystemParameters.VirtualScreenWidth, (int)SystemParameters.VirtualScreenHeight - 2,
                iconLayer: !m.ClickThrough);
            _running[m.Id] = new PluginRuntime { Manifest = m, Player = player, BuiltIn = IsBuiltIn(m) };
            player.Send(new Show());
            ReorderZ();
            Log.Information("插件已启动：{Id}（hwnd={Hwnd}）", m.Id, hwnd);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "插件启动失败：{Id}", m.Id);
        }
    }

    private static bool IsBuiltIn(PluginManifest m) =>
        m.Directory.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);

    public void Stop(string id)
    {
        if (!_running.TryGetValue(id, out var rt)) return;
        _running.Remove(id);
        try { rt.Player.Send(new DesktopManager.Ipc.Shutdown()); } catch { }
        rt.Player.Dispose();
        Log.Information("插件已停止：{Id}", id);
    }

    /// <summary>Z 序重排：插件窗口插到图标层正下方（壁纸仍在最底）。
    /// 图标层的 BottomPair/RequestReorder 链路触发全屏重排时会调到这里——幂等。</summary>
    public void ReorderZ()
    {
        foreach (var rt in _running.Values)
        {
            try
            {
                var iconHwnd = FindAnyIconLayerHwnd();
                if (iconHwnd != IntPtr.Zero)
                    WindowInterop.PlaceBelow((IntPtr)rt.Player.Hwnd, iconHwnd);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "插件 Z 序重排失败：{Id}", rt.Manifest.Id);
            }
        }
    }

    /// <summary>找一个在线图标层窗口作为 Z 序锚点（多屏任一即可；图标层间 Z 序由各自 BottomPair 维持）。</summary>
    public Func<IntPtr>? QueryIconLayerHwnd { get; set; }   // Z 序锚点提供者（host 注入）

    private IntPtr FindAnyIconLayerHwnd() => QueryIconLayerHwnd?.Invoke() ?? IntPtr.Zero;

    /// <summary>广播暂停/恢复（全屏/锁屏/电池治理；SupportsPause 才收）。</summary>
    public void BroadcastPause(bool pause)
    {
        foreach (var rt in _running.Values)
        {
            if (!rt.Manifest.SupportsPause) continue;
            try { rt.Player.Send(pause ? new Pause() : new Resume()); } catch { }
        }
    }

    /// <summary>插件消息处理（Dispatcher 线程）。</summary>
    private void OnMessage(string id, IpcMessage msg)
    {
        if (!_running.TryGetValue(id, out var rt)) return;
        switch (msg)
        {
            case PluginHello hello:
                rt.ReceivedHello = true;
                if (!string.Equals(hello.PluginId, id, StringComparison.Ordinal))
                    Log.Warning("插件握手 id 不符：清单 {Expected} vs 上报 {Actual}", id, hello.PluginId);
                break;

            case MonitorsReq:
                rt.Player.Send(new MonitorsInfo
                {
                    Monitors = MonitorEnumerator.Enumerate()
                        .Select(m => new PluginMonitorDto { X = m.X, Y = m.Y, W = m.Width, H = m.Height, IsPrimary = m.IsPrimary })
                        .ToList(),
                });
                break;

            case PluginConfigGet get:
                rt.Player.Send(new PluginConfigValue
                {
                    Key = get.Key,
                    Value = _getConfig().Configs.TryGetValue(id, out var cfg)
                        && cfg.TryGetValue(get.Key, out var v) ? v : null,
                });
                break;

            case PluginConfigSet set:
                SetPluginConfig(id, set.Key, set.Value);
                break;

            case PluginError err:
                Log.Error("插件[{Id}]：{Msg}", id, err.Message);
                Services.LogDb.Audit("plugin", "error", err.Message, id);
                break;
        }
    }

    /// <summary>写插件配置（内存 config + 立即落盘）。</summary>
    private void SetPluginConfig(string id, string key, string value)
    {
        // 由 MultiMonitorHost 通过 Mutation 回调注入（config 是它的字段，本类不持有写权）
        ConfigMutator?.Invoke(id, key, value);
        _saveNow();
    }

    /// <summary>配置写钩子（MultiMonitorHost 注入：改其 _pluginConfig 字段并进聚合快照）。</summary>
    public Action<string, string, string>? ConfigMutator { get; set; }

    /// <summary>启停持久化：改 config.Enabled 后调用。</summary>
    public void SetEnabled(string id, bool enabled)
    {
        EnabledMutator?.Invoke(id, enabled);
        _saveNow();
    }

    public Action<string, bool>? EnabledMutator { get; set; }

    public void Dispose()
    {
        foreach (var id in _running.Keys.ToList()) Stop(id);
    }
}
