using System.IO;
using Serilog;
using Serilog.Events;

namespace DesktopManager.App.Logging;

/// <summary>Serilog 按天 rolling 日志配置（M1 backlog P1）。
/// App.OnStartup 最早期调 <see cref="Init"/>；App.OnExit 末尾调 <see cref="Shutdown"/>（确保 flush）。
/// 日志路径：&lt;appbase&gt;/logs/log-YYYYMMDD.log（按天 rolling，保留 14 天，shared 允许多实例共享写）。</summary>
public static class LogConfig
{
    /// <summary>配置并初始化 <see cref="Log.Logger"/>。幂等多次调用安全（覆盖前一个 Logger）。</summary>
    public static void Init()
    {
        // AppContext.BaseDirectory 在所有启动场景（含 --restore-icons 单实例、AOT 友好）都可用，
        // 比 AppDomain.CurrentDomain.BaseDirectory 在某些 trim 场景更稳。
        string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        if (!Directory.Exists(logDir))
            Directory.CreateDirectory(logDir);

        // 模板：时间戳 毫秒 + [级别 3 字母大写] + 消息（字面量，避免 { } 结构化误解析）+ 空行 + 异常栈。
        const string template =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()              // 开发期 Debug；线上若嫌噪可改 Information 或加配置开关
            .WriteTo.File(
                path: Path.Combine(logDir, "log-.log"),
                outputTemplate: template,
                rollingInterval: RollingInterval.Day,  // 文件名 log-20260728.log
                retainedFileCountLimit: 14,            // 保留 14 天，避免无限增长
                shared: true,                         // 多实例（如 --restore-icons 与主进程同时存在）共享写
                rollOnFileSizeLimit: true,            // 极端情况单日超大文件也滚动
                fileSizeLimitBytes: 10 * 1024 * 1024) // 单文件 10MB 上限
            .CreateLogger();
    }

    /// <summary>关闭并 flush 所有挂起日志。App.OnExit 末尾调用（base.OnExit 之前）。</summary>
    public static void Shutdown() => Log.CloseAndFlush();
}
