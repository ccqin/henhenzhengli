namespace DesktopManager.Core.Models;

/// <summary>Core 侧显示器引用（纯数据，不依赖 Native/App）。
/// <see cref="PersistentId"/> = 布局归属唯一键（Native 层解析：adapter PCI 路径 + source id，
/// 换排列顺序/重启稳定，见 MonitorIdResolver）。<see cref="IsPrimary"/> 用于缺省归属（旧 config/新图标落主屏）。</summary>
public record MonitorRef(string PersistentId, bool IsPrimary);
