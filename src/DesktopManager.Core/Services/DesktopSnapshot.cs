using System.IO;
using DesktopManager.Core.Models;
namespace DesktopManager.Core.Services;

/// <summary>读取桌面文件夹（用户 Desktop + 公共 CommonDesktop）为 IconItem 快照。
/// 构造接受文件夹路径列表（便于测试注入 fixture）。</summary>
public sealed class DesktopSnapshot : IDesktopSnapshot
{
    private readonly string[] _folders;

    public DesktopSnapshot(params string[] folders) => _folders = folders ?? throw new ArgumentNullException(nameof(folders));

    /// <summary>默认：用户桌面 + 公共桌面。</summary>
    public static DesktopSnapshot ForDefaultDesktops() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));

    public IReadOnlyList<IconItem> Capture()
    {
        var items = new List<IconItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in _folders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (!seen.Add(path)) continue; // 按完整 FilePath 去重（同一物理文件被多个文件夹路径枚举到时只留一份；用户/公共桌面下不同路径的同名文件各自保留）
                items.Add(new IconItem(path, Path.GetFileName(path)));
            }
        }
        return items;
    }
}
