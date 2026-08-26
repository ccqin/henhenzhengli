using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

/// <summary>GPU 第三梯队·预处理转码：决策表 / ffprobe 解析 / 命令行构造 / 缓存键稳定性。</summary>
public class WallpaperTranscoderTests
{
    // ---------- 决策表 ----------

    [Fact] // 已达标：HEVC + ≤30fps + 不超宽 → 不转码
    public void Decide_AlreadyOptimal_NoTranscode()
    {
        var p = new VideoProbe("hevc", 30, 1920, 1080);
        Assert.False(Decide(p, 1920).Needed);
    }

    [Fact] // 非 HEVC（h264）→ 转码（无 fps/scale 需求时 plan 仍 Needed，仅换编码）
    public void Decide_H264_Transcode()
    {
        var p = new VideoProbe("h264", 30, 1920, 1080);
        Assert.True(Decide(p, null).Needed);
    }

    [Fact] // 59.94 → 降 30
    public void Decide_60Fps_DownTo30()
    {
        var p = new VideoProbe("h264", 60000.0 / 1001, 1920, 1080);
        Assert.Equal(30, Decide(p, null).Fps);
    }

    [Fact] // 30fps 不降（容差内）；恰好 32 也不降
    public void Decide_30Fps_NoChange()
    {
        Assert.Null(Decide(new VideoProbe("h264", 30, 1920, 1080), null).Fps);
        Assert.Null(Decide(new VideoProbe("h264", 32, 1920, 1080), null).Fps);
    }

    [Fact] // 超宽 → 缩到屏宽且偶数对齐
    public void Decide_Oversized_ScaleToEvenWidth()
    {
        var plan = Decide(new VideoProbe("h264", 30, 3840, 1080), 1919); // 奇数屏宽 → 对齐偶数
        Assert.Equal(1918, plan.ScaleW);
    }

    [Fact] // 组模式（maxW=null）：3840 宽不缩（跨屏拼接需要原始分辨率）
    public void Decide_GroupMode_NoScale()
    {
        Assert.Null(Decide(new VideoProbe("h264", 30, 3840, 1080), null).ScaleW);
    }

    // ---------- ffprobe 解析 ----------

    [Fact]
    public void ParseProbe_TypicalH264_60Fps()
    {
        var p = WallpaperTranscoder.ParseProbe(
            """{"streams":[{"codec_name":"h264","r_frame_rate":"60000/1001","width":3840,"height":1080}]}""");
        Assert.NotNull(p);
        Assert.Equal("h264", p.Codec);
        Assert.Equal(3840, p.Width);
        Assert.Equal(59.94, p.Fps, 2);
    }

    [Fact]
    public void ParseProbe_IntegerRate()
    {
        var p = WallpaperTranscoder.ParseProbe(
            """{"streams":[{"codec_name":"hevc","r_frame_rate":"30/1","width":1920,"height":1080}]}""");
        Assert.Equal(30, p.Fps, 0);
    }

    [Fact]
    public void ParseProbe_InvalidJson_ReturnsNull()
    {
        Assert.Null(WallpaperTranscoder.ParseProbe("not json"));
        Assert.Null(WallpaperTranscoder.ParseProbe("""{"streams":[]}"""));
    }

    [Fact]
    public void ParseRate_FractionAndPlain()
    {
        Assert.Equal(29.97, WallpaperTranscoder.ParseRate("30000/1001"), 2);
        Assert.Equal(25, WallpaperTranscoder.ParseRate("25"));
        Assert.Equal(0, WallpaperTranscoder.ParseRate("0/0"));
    }

    // ---------- 命令行构造 ----------

    [Fact]
    public void BuildArgs_Qsv_UsesHardwareEncoder()
    {
        var args = WallpaperTranscoder.BuildArgs("a.mp4", "out.mp4", new TranscodePlan(false, null, null), useQsv: true);
        Assert.Contains("hevc_qsv", args);
        Assert.Contains("-tag:v hvc1", args);   // hev1 会被 VLC demux 卡死（真机教训）
        Assert.Contains("-an", args);              // 壁纸无音频
        Assert.Contains("+faststart", args);       // mp4 索引前置
        Assert.DoesNotContain("-vf", args);        // 无需滤镜链
    }

    [Fact]
    public void BuildArgs_X265Fallback()
    {
        var args = WallpaperTranscoder.BuildArgs("a.mp4", "out.mp4", new TranscodePlan(false, null, null), useQsv: false);
        Assert.Contains("libx265", args);
        Assert.Contains("-tag:v hvc1", args);
    }

    [Fact]
    public void BuildArgs_ScaleAndFps_ComposedInOrder()
    {
        var args = WallpaperTranscoder.BuildArgs("a.mp4", "out.mp4", new TranscodePlan(false, 1920, 30), useQsv: true);
        Assert.Contains("-vf \"scale=1920:-2,fps=30\"", args);
    }

    // ---------- 缓存键 ----------

    [Fact]
    public void CachePath_StableForSameInput_DifferentForDifferentPlan()
    {
        var t = new WallpaperTranscoder(null, Path.Combine(Path.GetTempPath(), "dm-tc-test"));
        var a1 = t.CachePath(@"C:\w\a.mp4", new TranscodePlan(true, null, 30));
        var a2 = t.CachePath(@"C:\w\a.mp4", new TranscodePlan(true, null, 30));
        var a3 = t.CachePath(@"C:\w\a.mp4", new TranscodePlan(false, 1920, 30));
        Assert.Equal(a1, a2);          // 同参数稳定（跨会话命中）
        Assert.NotEqual(a1, a3);       // 参数变（如换屏/阈值调整）自然失效
        Assert.EndsWith(".mp4", a1);
    }

    private static TranscodePlan Decide(VideoProbe p, int? maxW) => WallpaperTranscoder.Decide(p, maxW);
}
