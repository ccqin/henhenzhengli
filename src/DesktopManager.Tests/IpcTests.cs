using System.IO;
using System.Text;
using DesktopManager.Ipc;
using Xunit;

namespace DesktopManager.Tests;

public class IpcTests
{
    private static (IpcMessage roundTripped, string json) RoundTrip(IpcMessage msg)
    {
        using var ms = new MemoryStream();
        IpcWriter.Write(ms, msg);
        ms.Position = 0;
        var json = Encoding.UTF8.GetString(ms.ToArray());
        using var reader = IpcReader.OpenReader(ms);
        var back = IpcReader.ReadAsync(reader).Result;
        return (roundTripped: back!, json);
    }

    [Fact]
    public void Ready_RoundTrip()
    {
        var (msg, json) = RoundTrip(new Ready { Hwnd = 12345 });
        var ready = Assert.IsType<Ready>(msg);
        Assert.Equal(12345, ready.Hwnd);
        Assert.Contains("\"type\":\"ready\"", json);
        Assert.Contains("\"v\":1", json);
    }

    [Fact]
    public void SetWallpaper_RoundTrip_WithCrop()
    {
        var (msg, _) = RoundTrip(new SetWallpaper
        {
            Path = @"C:\w\img.jpg",
            Kind = "video",
            CropX = -1920,
            CropY = 0,
            CropW = 3840,
            CropH = 1080,
            CanvasW = 3840,
            CanvasH = 1080,
        });
        var w = Assert.IsType<SetWallpaper>(msg);
        Assert.Equal(@"C:\w\img.jpg", w.Path);
        Assert.Equal("video", w.Kind);
        Assert.Equal(-1920, w.CropX);
        Assert.Equal(3840, w.CanvasW);
    }

    [Fact]
    public void SetIcons_ApplyDiff_RoundTrip()
    {
        var icon = new IconDto { Path = @"C:\Users\a\Desktop\foo.txt", Name = "foo", X = 10, Y = 20, FenceId = "f1" };
        var (setMsg, _) = RoundTrip(new SetIcons { Items = [icon] });
        var set = Assert.IsType<SetIcons>(setMsg);
        Assert.Single(set.Items);
        Assert.Equal("f1", set.Items[0].FenceId);

        var (diffMsg, _) = RoundTrip(new ApplyDiff
        {
            Added = [icon with { Path = @"C:\Users\a\Desktop\bar.txt" }],
            Removed = [@"C:\Users\a\Desktop\baz.txt"],
        });
        var diff = Assert.IsType<ApplyDiff>(diffMsg);
        Assert.Single(diff.Added);
        Assert.Single(diff.Removed);
    }

    [Fact]
    public void LayoutChanged_RoundTrip()
    {
        var (msg, _) = RoundTrip(new LayoutChanged
        {
            Fences = [new FenceDto { Id = "f1", Title = "游戏", X = 1, Y = 2, W = 300, H = 200, Collapsed = true }],
            Positions = [new IconPosDto { Path = @"C:\x\a.txt", X = 5, Y = 6 }],
        });
        var lc = Assert.IsType<LayoutChanged>(msg);
        var fence = Assert.Single(lc.Fences);
        Assert.Equal("游戏", fence.Title);
        Assert.True(fence.Collapsed);
        Assert.Single(lc.Positions);
    }

    [Theory]
    [InlineData(typeof(Pause), "pause")]
    [InlineData(typeof(Resume), "resume")]
    [InlineData(typeof(Show), "show")]
    [InlineData(typeof(Shutdown), "shutdown")]
    public void MarkerMessages_RoundTrip(Type type, string typeName)
    {
        var msg = (IpcMessage)Activator.CreateInstance(type)!;
        var (back, json) = RoundTrip(msg);
        Assert.Equal(type, back.GetType());
        Assert.Contains($"\"type\":\"{typeName}\"", json);
    }

    [Fact]
    public void SetPosition_Error_IconOpened_RoundTrip()
    {
        var (pos, _) = RoundTrip(new SetPosition { X = -1920, Y = 0, W = 1920, H = 1080 });
        Assert.Equal(-1920, Assert.IsType<SetPosition>(pos).X);

        var (err, _) = RoundTrip(new Error { Message = "boom" });
        Assert.Equal("boom", Assert.IsType<Error>(err).Message);

        var (opened, _) = RoundTrip(new IconOpened { Path = @"C:\x\a.txt" });
        Assert.Equal(@"C:\x\a.txt", Assert.IsType<IconOpened>(opened).Path);
    }

    [Fact]
    public void Reader_SkipsBlankLines_AndReturnsNullOnEof()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("\n  \n{\"type\":\"pause\",\"v\":1}\n"));
        ms.Position = 0;
        using var reader = IpcReader.OpenReader(ms);
        var first = IpcReader.ReadAsync(reader).Result;
        Assert.IsType<Pause>(first);
        var second = IpcReader.ReadAsync(reader).Result;
        Assert.Null(second);
    }
}
