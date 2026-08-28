using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopManager.Core.Services;

/// <summary>视频流探测结果（ffprobe）。</summary>
public sealed record VideoProbe(string Codec, double Fps, int Width, int Height);

/// <summary>转码决策（纯函数，单测覆盖）：三项全空/假 = 无需转码。</summary>
public sealed record TranscodePlan(bool NeedHevc, int? ScaleW, int? Fps)
{
    public bool Needed => NeedHevc || ScaleW is not null || Fps is not null;
}

/// <summary>视频壁纸转码器（GPU 第三梯队·预处理）：设置视频壁纸时后台 ffmpeg 转
/// HEVC@≤30fps（超屏时附缩放），产出解码负载更低的缓存副本——VLC/MediaElement 播放均适用。
/// 策略：先用原文件立即播放（零等待），转码完成后由调用方重分发切到缓存版。
/// 编码 hevc_qsv 优先（Intel 核显快），失败回退 libx265。工具缺失 → 功能整体关闭（优雅降级）。</summary>
public sealed class WallpaperTranscoder : IDisposable
{
    private const int FpsTolerance = 2; // fps 超过 30+FpsTolerance 才降（59.94/60 降，30/1 不动）

    private readonly string _ffmpeg;
    private readonly string _cacheRoot;
    private readonly Dictionary<string, VideoProbe> _probeCache = new();   // key: 绝对路径|mtime
    private readonly Dictionary<string, byte> _inflight = new();           // 去重并发转码
    private readonly HashSet<Process> _running = new();                    // 持引用防 GC（Exited 事件要求实例存活）

    public WallpaperTranscoder(string? toolsDir, string cacheRoot)
    {
        // 工具查找：toolsDir（应用/包目录）→ %APPDATA%\DesktopManager\tools
        // 只需 ffmpeg.exe：探测用 `ffmpeg -i` 的 stderr 解析（无需 ffprobe，省一半工具体积）
        var probeDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(toolsDir)) probeDirs.Add(toolsDir);
        var userTools = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopManager", "tools");
        probeDirs.Add(userTools);
        _ffmpeg = probeDirs.Select(d => Path.Combine(d, "ffmpeg.exe")).FirstOrDefault(File.Exists) ?? "";
        _cacheRoot = cacheRoot;
        Directory.CreateDirectory(cacheRoot);
        CleanupStale();
    }

    /// <summary>ffmpeg 就绪（缺则功能整体关闭）。</summary>
    public bool Available => _ffmpeg.Length > 0;

    /// <summary>缓存 LRU：命中即 Touch（活跃缓存永不过期），启动时清理 14 天未访问的陈旧文件
    /// （换壁纸后旧缓存自然过期；误删活跃缓存仅代价一次重转码，几秒）。</summary>
    private void CleanupStale()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-14);
            foreach (var f in Directory.EnumerateFiles(_cacheRoot, "*.mp4"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
                }
                catch { /* 单文件失败忽略 */ }
            }
        }
        catch { /* 目录不可读忽略 */ }
    }


    public string CacheRoot => _cacheRoot;

    /// <summary>决策：HEVC 已达标（编码 hevc + fps≤阈值 + 不超宽）→ 无需转码；
    /// 否则按需给：换 HEVC 编码 + 缩放宽（maxW 内，偶数对齐 x265）+ 目标帧率 30。</summary>
    public static TranscodePlan Decide(VideoProbe p, int? maxW)
    {
        int? scaleW = maxW is { } w && w > 0 && p.Width > w ? w - (w % 2) : null;
        int? fps = p.Fps > 30 + FpsTolerance ? 30 : null;
        bool needHevc = p.Codec is not ("hevc" or "hvc1");
        return new TranscodePlan(needHevc, scaleW, fps);
    }

    /// <summary>一步式解析播放路径：无需转码→null；有缓存→缓存路径；
    /// 否则后台启动转码（完成触发 onReady，线程池线程）并返回 null（先用原文件播）。</summary>
    public string? ResolvePlaybackPath(string srcPath, int? maxW, Action? onReady)
    {
        if (!Available) return null;
        var probe = Probe(srcPath);
        if (probe is null) return null;
        var plan = Decide(probe, maxW);
        if (!plan.Needed) return null;

        var cached = CachePath(srcPath, plan);
        if (File.Exists(cached))
        {
            try { File.SetLastWriteTimeUtc(cached, DateTime.UtcNow); } catch { } // LRU Touch
            return cached;
        }
        StartTranscode(srcPath, cached, plan, onReady);
        return null;
    }

    // ---------- ffprobe ----------

    private VideoProbe? Probe(string path)
    {
        var key = ProbeKey(path);
        if (_probeCache.TryGetValue(key, out var cached)) return cached;

        // `ffmpeg -i` 无输出参数 → 退出码 1（"At least one output file..."），流信息在 stderr——正常路径
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg,
            Arguments = $"-hide_banner -i \"{path}\"",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return null;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(5000);

        var probe = ParseFfmpegProbe(stderr);
        if (probe is not null) _probeCache[key] = probe;
        return probe;
    }

    /// <summary>解析 `ffmpeg -i` 的 stderr 流信息行（纯函数，单测覆盖）。典型：
    /// <code>Stream #0:0[0x1]: Video: h264 (High) (avc1 / 0x31637661), yuv420p, 3840x1080 [SAR 1:1 DAR 32:9], 786 kb/s, 24 fps, 24 tbr, ...</code>
    /// fps 缺失时回退 tbr；无 Video 行/解析失败返回 null。</summary>
    public static VideoProbe? ParseFfmpegProbe(string stderr)
    {
        var line = stderr.Split('\n').FirstOrDefault(l => l.Contains(": Video: "));
        if (line is null) return null;
        var codec = Regex.Match(line, @"Video: (\w+)");
        var dim = Regex.Match(line, @"(\d{2,5})x(\d{2,5})");
        if (!codec.Success || !dim.Success) return null;
        var fps = Regex.Match(line, @"([\d.]+) fps") is { Success: true } f
            ? f : Regex.Match(line, @"([\d.]+) tbr");
        if (!int.TryParse(dim.Groups[1].Value, out var w) || !int.TryParse(dim.Groups[2].Value, out var h)) return null;
        var rate = fps.Success ? double.Parse(fps.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        if (w <= 0 || h <= 0 || rate <= 0) return null;
        return new VideoProbe(codec.Groups[1].Value, rate, w, h);
    }


    private static string ProbeKey(string path)
    {
        try { return Path.GetFullPath(path) + "|" + File.GetLastWriteTimeUtc(path).Ticks; }
        catch { return path + "|0"; }
    }

    // ---------- 缓存 ----------

    /// <summary>缓存路径：&lt;sha16(源路径+mtime+参数)&gt;_源名.mp4——参数变（新决策）自然换键。</summary>
    public string CachePath(string srcPath, TranscodePlan plan)
    {
        string src = Path.GetFileName(srcPath);
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(
            $"{AlgoVersion}|{srcPath}|{ProbeKey(srcPath)}|hevc={plan.NeedHevc}|scale={plan.ScaleW}|fps={plan.Fps}")))[..12].ToLowerInvariant();
        return Path.Combine(_cacheRoot, $"{hash}_{Path.GetFileNameWithoutExtension(src)}.mp4");
    }

    // ---------- 转码进程 ----------

    private void StartTranscode(string src, string dst, TranscodePlan plan, Action? onReady)
    {
        lock (_inflight)
        {
            if (_inflight.ContainsKey(dst)) return;
            _inflight[dst] = 0;
        }
        var tmp = dst + ".tmp.mp4";
        // qsv 先试（核显硬编码，4K 宽壁纸秒级~分钟级）；失败（无核显/驱动不支持）回退 libx265 软编
        RunFfmpeg(src, tmp, plan, useQsv: true, () =>
        {
            if (File.Exists(tmp) && new FileInfo(tmp).Length > 1024) Finish(tmp, dst, onReady);
            else RunFfmpeg(src, tmp, plan, useQsv: false, () =>
            {
                if (File.Exists(tmp) && new FileInfo(tmp).Length > 1024) Finish(tmp, dst, onReady);
                else Cleanup(dst);
            });
        });
    }

    private void Finish(string tmp, string dst, Action? onReady)
    {
        try
        {
            File.Move(tmp, dst); // 同目录原子改名；同键并发由 _inflight 去重
        }
        catch { Cleanup(dst); return; }
        lock (_inflight) _inflight.Remove(dst);
        onReady?.Invoke();
    }

    private void Cleanup(string dst)
    {
        lock (_inflight) _inflight.Remove(dst);
        try { File.Delete(dst + ".tmp.mp4"); } catch { }
    }

    /// <summary>转码算法版本（进缓存键）：转码参数/管线变更时递增 → 旧缓存自动失效。
    /// v1→v2：qsv 补 -tag:v hvc1（hev1 参数集带内，VLC 3.x demux 卡死 → 播放无帧 + 子进程内存暴涨，真机教训）。</summary>
    private const string AlgoVersion = "v2";

    /// <summary>构造 ffmpeg 参数（qsv / x265 两套编码器 + 按需 vf 链）。
    /// HEVC in MP4 必须 hvc1 tag（参数集进 sample entry）：VLC 3.x 对 hev1（带内）demux 不兼容。</summary>
    public static string BuildArgs(string src, string dst, TranscodePlan plan, bool useQsv)
    {
        var vf = new List<string>();
        if (plan.ScaleW is { } w) vf.Add($"scale={w}:-2");   // -2 保宽高比且高度偶数
        if (plan.Fps is { } fps) vf.Add($"fps={fps}");
        string filters = vf.Count > 0 ? $"-vf \"{string.Join(',', vf)}\" " : "";
        string codec = useQsv
            ? "-c:v hevc_qsv -preset veryfast -global_quality 30 -pix_fmt nv12 -tag:v hvc1"
            : "-c:v libx265 -preset veryfast -crf 28 -pix_fmt yuv420p -tag:v hvc1";
        return $"-hide_banner -loglevel error -y -i \"{src}\" {filters}{codec} -an -movflags +faststart -f mp4 \"{dst}\"";
    }

    private void RunFfmpeg(string src, string dst, TranscodePlan plan, bool useQsv, Action onExit)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpeg,
                Arguments = BuildArgs(src, dst, plan, useQsv),
                UseShellExecute = false, CreateNoWindow = true,
            };
            var p = Process.Start(psi);
            if (p is null) { onExit(); return; }
            lock (_running) _running.Add(p);
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                lock (_running) _running.Remove(p);
                using (p) { }
                onExit();
            };
        }
        catch { onExit(); }
    }

    public void Dispose()
    {
        lock (_running)
            foreach (var p in _running)
                try { p.Kill(); } catch { }
    }
}
