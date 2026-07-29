using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DesktopManager.Core.Models;

/// <summary>桌面图标数据模型（P0 数据驱动渲染）。
/// 可观察：X/Y/FilePath/DisplayName 的 setter 触发 INPC，供 ItemsControl 的 ItemContainerStyle 绑定 Canvas.Left/Top 等。
/// 不序列化（ConfigStore 只存 FenceConfig.IconFilePaths；IconItem 从不进 JSON）。
/// 构造签名保持 (filePath, displayName, x=0, y=0) —— DesktopDiffTests/DesktopSnapshot 依赖。</summary>
public sealed class IconItem : INotifyPropertyChanged
{
    private string _filePath;
    private string _displayName;
    private double _x;
    private double _y;

    public IconItem(string filePath, string displayName, double x = 0, double y = 0)
    {
        _filePath = filePath;
        _displayName = displayName;
        _x = x;
        _y = y;
    }

    public string FilePath
    {
        get => _filePath;
        set => Set(ref _filePath, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => Set(ref _displayName, value);
    }

    public double X
    {
        get => _x;
        set => Set(ref _x, value);
    }

    public double Y
    {
        get => _y;
        set => Set(ref _y, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>等值短路赋值；变更时触发 INPC。返回是否实际发生变更。
    /// private：IconItem 为 sealed 叶子模型，Set 仅供本类 4 个 setter 复用。</summary>
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
