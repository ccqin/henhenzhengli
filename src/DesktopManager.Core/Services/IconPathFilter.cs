namespace DesktopManager.Core.Services;

/// <summary>
/// 加载 <see cref="Models.FenceConfig.IconFilePaths"/> 时过滤掉已不存在的路径
/// （用户可能在 app 关闭后删除了文件），避免渲染/归属时崩溃。
/// 抽到 Core 以便单测容错逻辑（不依赖 WPF / FenceControl）。
/// 与 IconLayerWindow._fencedPaths 一致使用 OrdinalIgnoreCase 去重。
/// </summary>
public static class IconPathFilter
{
    public static IReadOnlyList<string> FilterExisting(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                // File.Exists 对非法路径/权限错误返回 false 不抛；try/catch 兜底极端 case。
                if (!File.Exists(p)) continue;
            }
            catch
            {
                // 路径非法等异常 → 跳过，不崩。
                continue;
            }
            if (seen.Add(p)) result.Add(p);
        }
        return result;
    }
}
