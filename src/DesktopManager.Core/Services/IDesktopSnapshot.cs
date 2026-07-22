using DesktopManager.Core.Models;
namespace DesktopManager.Core.Services;

public interface IDesktopSnapshot
{
    IReadOnlyList<IconItem> Capture();
}
