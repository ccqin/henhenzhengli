using System.Globalization;
using System.Windows.Data;
using DesktopManager.App.Services;

namespace DesktopManager.App.Converters;

/// <summary>
/// P0-T2：FilePath → WPF 图标 ImageSource 绑定转换器。
/// 供散落图标 DataTemplate 的 <c>Image.Source="{Binding FilePath, Converter=...}"</c> 使用，
/// 让 ItemsControl 数据驱动渲染时通过 Binding 取图标（替代 M2 SetIcons 命令式 <c>_icons.GetIcon</c>）。
///
/// 持有共享 <see cref="IconExtractor"/>（宿主 IconLayerWindow 构造注入，与所有 FenceControl 共用同一份图标缓存）。
/// 避免每个 Converter 自建缓存实例（Layouter 反面教材：FilePathToIconConverter 无缓存每次重提 SHGetFileInfo）。
/// 宿主在 <c>InitializeComponent</c> 前把本类实例注册为 Window Resources 的 <c>FilePathToIconConverter</c>。
/// </summary>
public sealed class FilePathToIconConverter : IValueConverter
{
    private readonly IconExtractor _icons;

    public FilePathToIconConverter(IconExtractor icons)
    {
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
    }

    /// <summary>filePath → BitmapSource（IconExtractor 内部带路径缓存 + Freeze 跨线程）。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path)) return null;
        return _icons.GetIcon(path); // null（提取失败）由 Image.Source 容忍 → 显示空
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException($"{nameof(FilePathToIconConverter)} 为单向转换。");
}
