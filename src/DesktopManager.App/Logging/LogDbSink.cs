using Serilog.Core;
using Serilog.Events;
using DesktopManager.App.Services;

namespace DesktopManager.App.Logging;

/// <summary>Serilog → LogDb sink：INF 及以上级别入库（DBG 仅文件，量太大）。
/// source 用日志上下文属性 SourceContext（无则 "App"）。</summary>
public sealed class LogDbSink : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Information) return;
        var level = logEvent.Level switch
        {
            LogEventLevel.Information => "INF",
            LogEventLevel.Warning => "WRN",
            LogEventLevel.Error => "ERR",
            LogEventLevel.Fatal => "ERR",
            _ => "DBG",
        };
        string source = "App";
        if (logEvent.Properties.TryGetValue("SourceContext", out var sc))
            source = sc.ToString().Trim('"');
        var msg = logEvent.RenderMessage();
        if (logEvent.Exception is not null)
            msg += " | " + logEvent.Exception.GetType().Name + ": " + logEvent.Exception.Message;
        LogDb.WriteLog(
            logEvent.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            level, source, msg);
    }
}
