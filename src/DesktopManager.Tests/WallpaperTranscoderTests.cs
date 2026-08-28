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

    // ---------- `ffmpeg -i` stderr 解析（替代 ffprobe，省一半工具体积） ----------

    [Fact] // 典型 h264 行：codec/尺寸/fps 全解析
    public void ParseFfmpegProbe_TypicalH264()
    {
        var p = WallpaperTranscoder.ParseFfmpegProbe(
            "  Stream #0:0[0x1]: Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, bt709, progressive), 3840x1080 [SAR 1:1 DAR 32:9], 786 kb/s, 24 fps, 24 tbr, 24k tbn, 48k tbc");
        Assert.NotNull(p);
        Assert.Equal("h264", p.Codec);
        Assert.Equal(3840, p.Width);
        Assert.Equal(1080, p.Height);
        Assert.Equal(24, p.Fps, 0);
    }

    [Fact] // 小数帧率 + hevc
    public void ParseFfmpegProbe_HevcFractionalFps()
    {
        var p = WallpaperTranscoder.ParseFfmpegProbe(
            "  Stream #0:0[0x1]: Video: hevc (Main 10) (hvc1 / 0x31766368), yuv420p10le, 1920x1080 [SAR 1:1 DAR 16:9], 2345 kb/s, 29.97 fps, 29.97 tbr, 30k tbn");
        Assert.NotNull(p);
        Assert.Equal("hevc", p.Codec);
        Assert.Equal(29.97, p.Fps, 2);
    }

    [Fact] // fps 缺失回退 tbr
    public void ParseFfmpegProbe_FallsBackToTbr()
    {
        var p = WallpaperTranscoder.ParseFfmpegProbe(
            "  Stream #0:1[0x1]: Video: mpeg4 (Simple Profile) (mp4v / 0x76333434), yuv420p, 640x480 [SAR 1:1 DAR 4:3], 800 kb/s, 25 tbr, 25k tbn");
        Assert.NotNull(p);
        Assert.Equal(25, p.Fps, 0);
    }

    [Fact] // 完整 stderr（含音频行 + Input/Duration 等噪声行）
    public void ParseFfmpegProbe_FullStderrSkipsAudioLine()
    {
        var p = WallpaperTranscoder.ParseFfmpegProbe(
            "Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'a.mp4':\r\n" +
            "  Duration: 00:00:15.04, start: 0.000000, bitrate: 839 kb/s\r\n" +
            "    Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p, 3840x1080, 786 kb/s, 24 fps\r\n" +
            "    Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6D703461), 48000 Hz, stereo, fltp, 61 kb/s\r\n" +
            "At least one output file must be specified\r\n");
        Assert.NotNull(p);
        Assert.Equal("h264", p.Codec);
        Assert.Equal(3840, p.Width);
    }

    [Fact] // 无 Video 行 / 只有音频 → null
    public void ParseFfmpegProbe_NoVideoLine_ReturnsNull()
    {
        Assert.Null(WallpaperTranscoder.ParseFfmpegProbe("At least one output file must be specified"));
        Assert.Null(WallpaperTranscoder.ParseFfmpegProbe(
            "  Stream #0:1[0x2](und): Audio: aac (LC), 48000 Hz, stereo\r\n"));
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
