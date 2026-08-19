namespace DesktopManager.Core.Services;

/// <summary>图标路由纯函数（M6 从 MultiMonitorHost 下沉，可单测）：
/// 图标按「子进程持有（fence/散落）→ config 位置 hint → 主屏」三级归属路由；孤儿 path 跳过。</summary>
public static class IconRouter
{
    /// <summary>路由单个图标的目标屏。ownerOf：运行时持有查询（fence 归属或散落）；
    /// hints：config 位置记录解析出的在线归属；primary：主屏 ID。返回 null = 无处可去。</summary>
    public static string? Route(
        string path,
        Func<string, string?> ownerOf,
        IReadOnlyDictionary<string, string> hints,
        string? primary,
        IReadOnlySet<string>? orphanPaths = null)
    {
        if (orphanPaths?.Contains(path) == true) return null;
        var owner = ownerOf(path);
        if (owner is not null) return owner;
        if (hints.TryGetValue(path, out var hint)) return hint;
        return primary;
    }

    /// <summary>为某屏切分图标全集（SplitFor 语义：只留目标为该屏的项）。</summary>
    public static List<Models.IconItem> SplitFor(
        string monitorId,
        IEnumerable<Models.IconItem> all,
        Func<string, string?> ownerOf,
        IReadOnlyDictionary<string, string> hints,
        string? primary,
        IReadOnlySet<string>? orphanPaths = null)
    {
        var result = new List<Models.IconItem>();
        foreach (var item in all)
        {
            if (Route(item.FilePath, ownerOf, hints, primary, orphanPaths) == monitorId)
                result.Add(item);
        }
        return result;
    }
}
