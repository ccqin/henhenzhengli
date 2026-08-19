# -*- coding: utf-8 -*-
import re
p = 'src/DesktopManager.App/MultiMonitorHost.cs'
s = open(p, encoding='utf-8').read()

s = s.replace('''    // 防抖保存：任一子进程 LayoutChanged → 500ms 后聚合落盘。
    private readonly System.Threading.Timer _saveTimer;
    private readonly object _saveLock = new();
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);
    private volatile bool _savingDisabled;''', '''    // M6 拆分：持久化委托给 PersistenceService（防抖/立即保存）。
    private readonly Services.PersistenceService _persistence;''')

s = s.replace('''    public MultiMonitorHost(IConfigStore store)
    {
        _store = store;
        _saveTimer = new System.Threading.Timer(_ => OnSaveTimerElapsed(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);''', '''    public MultiMonitorHost(IConfigStore store)
    {
        _store = store;
        _persistence = new Services.PersistenceService(store, BuildAggregatedConfig);''')

m = re.search(r'    private void RequestSave\(\)\n    \{.*?\n    \}\n\n    private void OnSaveTimerElapsed\(\)\n    \{.*?\n    \}\n', s, re.S)
assert m, 'save methods'
s = s.replace(m.group(0), '    private void RequestSave() => _persistence.RequestSave();\n')

m2 = re.search(r'    /// <summary>立即聚合保存（不等防抖）。OnExit 调用；随后 CloseAll。</summary>\n    public void SaveAllNow\(\)\n    \{.*?\n    \}\n', s, re.S)
assert m2, 'SaveAllNow'
s = s.replace(m2.group(0), '    /// <summary>立即聚合保存（不等防戏）。OnExit 调用；随后 CloseAll。</summary>\n    public void SaveAllNow() => _persistence.SaveNow();\n'.replace('防戏', '防抖'))

s = s.replace('''        AppConfig snapshot;
        try
        {
            snapshot = BuildAggregatedConfig();
            lock (_saveLock)
            {
                if (!_savingDisabled) _store.Save(snapshot);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RebuildToMatchTopology：重建前保存失败，放弃重建（保现状）");
            return;
        }''', '''        AppConfig snapshot;
        try { snapshot = _persistence.SaveAndReturn(); }
        catch (Exception ex)
        {
            Log.Error(ex, "RebuildToMatchTopology：重建前保存失败，放弃重建（保现状）");
            return;
        }''')

old3 = '''        // 3. 以 snapshot 为新基线重算孤儿 + 每屏归属（孤儿集合整体重置）。
        _orphanFences.Clear(); _orphanPositions.Clear(); _orphanPaths.Clear();
        var fenceAssign = MonitorAssignment.FenceAssignments(snapshot.Fences, online);
        var looseAssign = MonitorAssignment.LooseAssignments(snapshot.IconPositions, online);
        foreach (var f in snapshot.Fences)
        {
            if (fenceAssign[f.Id] is not null) continue;
            _orphanFences.Add(f);
            foreach (var p in f.IconFilePaths) _orphanPaths.Add(p);
        }
        foreach (var p in snapshot.IconPositions)
        {
            if (looseAssign[p.FilePath] is not null) continue;
            _orphanPositions.Add(p);
            _orphanPaths.Add(p.FilePath);
        }
        _looseAssignHint = looseAssign
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase);
        var myFencesByMon = monitors.ToDictionary(m => m.PersistentId, _ => new List<FenceConfig>());
        var myPositionsByMon = monitors.ToDictionary(m => m.PersistentId, _ => new List<IconPosition>());
        foreach (var f in snapshot.Fences)
        {
            var a = fenceAssign[f.Id];
            if (a is not null) myFencesByMon[a].Add(f);
        }
        foreach (var p in snapshot.IconPositions)
        {
            var a = looseAssign[p.FilePath];
            if (a is not null) myPositionsByMon[a].Add(p);
        }'''
new3 = '''        // 3. 以 snapshot 为新基线重算孤儿 + 每屏归属（Core.TopologyRebuild 纯函数，单测覆盖）。
        var calc = TopologyRebuild.Calculate(snapshot, liveIds);
        _orphanFences.Clear(); _orphanFences.AddRange(calc.OrphanFences);
        _orphanPositions.Clear(); _orphanPositions.AddRange(calc.OrphanPositions);
        _orphanPaths.Clear(); _orphanPaths.UnionWith(calc.OrphanPaths);
        _looseAssignHint = calc.LooseHints;
        var myFencesByMon = calc.FencesByMon;
        var myPositionsByMon = calc.PositionsByMon;'''
assert old3 in s, 'rebuild seg3'
s = s.replace(old3, new3, 1)

old4 = '''    private (WallpaperConfig? Cfg, DisplayGroup? Group) ResolveWallpaper(string monitorId)
    {
        var g = _displayGroups.FirstOrDefault(g =>
            !string.IsNullOrWhiteSpace(g.WallpaperPath) && g.MonitorIds.Contains(monitorId));
        if (g is not null)
            return (new WallpaperConfig { MonitorId = monitorId, Kind = g.WallpaperKind, Path = g.WallpaperPath }, g);
        return (_wallpapers.FirstOrDefault(w => string.Equals(w.MonitorId, monitorId, StringComparison.Ordinal)), null);
    }'''
new4 = '''    private (WallpaperConfig? Cfg, DisplayGroup? Group) ResolveWallpaper(string monitorId)
        => WallpaperResolver.Resolve(monitorId, _wallpapers, _displayGroups);'''
assert old4 in s, 'resolve'
s = s.replace(old4, new4, 1)

old5 = '''        // M5-T3：组模式虚拟画布 = 组内在线成员 rect 的 bounding box；在线成员 <2 → 降级单屏。
        IntRect? canvas = null;
        IntRect? monRect = null;
        if (group is not null)
        {
            var online = MonitorEnumerator.Enumerate()
                .Where(m => group.MonitorIds.Contains(m.PersistentId))
                .ToList();
            if (online.Count >= 2)
            {
                var rects = online.Select(m => new IntRect(m.X, m.Y, m.X + m.Width, m.Y + m.Height)).ToList();
                canvas = CrossScreenLayout.Canvas(rects);
                var me = online.First(x => x.PersistentId == monitorId);
                monRect = new IntRect(me.X, me.Y, me.X + me.Width, me.Y + me.Height);
            }
        }'''
new5 = '''        // M5-T3：组模式虚拟画布（Core.WallpaperResolver 纯函数，单测覆盖）；在线成员 <2 → 降级单屏。
        IntRect? canvas = null;
        IntRect? monRect = null;
        if (group is not null)
        {
            var onlineRects = MonitorEnumerator.Enumerate()
                .Where(m => group.MonitorIds.Contains(m.PersistentId))
                .Select(m => (m.PersistentId, new IntRect(m.X, m.Y, m.X + m.Width, m.Y + m.Height)))
                .ToList();
            var cc = WallpaperResolver.CalcCanvas(monitorId, onlineRects);
            if (cc is { } v) { canvas = v.Canvas; monRect = v.MonRect; }
        }'''
assert old5 in s, 'canvas'
s = s.replace(old5, new5, 1)

old6 = re.search(r'    /// <summary>为某屏切分图标全集.*?\n    private List<IconDto> SplitFor\(string monitorId, IEnumerable<IconItem> all\)\n    \{.*?\n    \}\n', s, re.S)
assert old6, 'SplitFor'
new6 = ('    private List<IconDto> SplitFor(string monitorId, IEnumerable<IconItem> all) =>\n'
        '        IconRouter.SplitFor(monitorId, all, FindOwnerMonitor, _looseAssignHint, PrimaryMonitorId, _orphanPaths)\n'
        '            .Select(ToDto).ToList();\n')
s = s.replace(old6.group(0), new6)

open(p, 'w', encoding='utf-8').write(s)
print('host refactor ok')

# Core WallpaperResolver null 性修正
p2 = 'src/DesktopManager.Core/Services/WallpaperResolver.cs'
s2 = open(p2, encoding='utf-8').read()
s2 = s2.replace('        var me = screens.FirstOrDefault(s => s.Id == monitorId);\n        if (me.Id is null && me.Rect is null && !screens.Any(s => s.Id == monitorId)) return null;',
'''        var me = screens.FirstOrDefault(s => s.Id == monitorId);
        if (!screens.Any(s => s.Id == monitorId)) return null;''')
open(p2, 'w', encoding='utf-8').write(s2)
print('resolver null fix')
