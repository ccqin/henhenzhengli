using DesktopManager.Core.Models;
using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

public class DesktopSnapshotTests
{
    [Fact]
    public void Capture_ReturnsFilesFromFolder()
    {
        var dir = Directory.CreateTempSubdirectory("dm_snap_");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.txt"), "");
            File.WriteAllText(Path.Combine(dir.FullName, "b.lnk"), "");
            var snap = new DesktopSnapshot(dir.FullName);
            var items = snap.Capture();
            Assert.Equal(2, items.Count);
            Assert.Contains(items, i => i.DisplayName == "a.txt");
            Assert.Contains(items, i => i.FilePath.EndsWith("b.lnk"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Capture_EmptyFolder_ReturnsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory("dm_empty_");
        try
        {
            var snap = new DesktopSnapshot(dir.FullName);
            Assert.Empty(snap.Capture());
        }
        finally { dir.Delete(recursive: true); }
    }
}
