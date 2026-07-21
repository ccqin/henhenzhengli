using DesktopManager.Core.Models;
namespace DesktopManager.Core.Services;

public interface IConfigStore
{
    AppConfig Load();
    void Save(AppConfig config);
}
