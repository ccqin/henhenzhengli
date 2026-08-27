using System.IO;
using Microsoft.Data.Sqlite;

namespace DesktopManager.App.Services;

/// <summary>一行日志/操作记录（设置窗口日志页的数据模型）。</summary>
public record LogRow(long Id, string Ts, string Level, string Source, string Message);

/// <summary>日志数据库（SQLite，%AppData%\DesktopManager\logs.db）。
/// 两张表：logs（运行日志，Serilog sink 写入）+ ops（用户操作审计，主进程统一写入——
/// 子进程经 IPC 上报后由主进程落库，保持单一写者避免 SQLite 锁）。
/// 启动时滚动清理（30 天 / 2 万条封顶）。</summary>
public static class LogDb
{
    private static readonly object Lock = new();
    private static string? _connString;

    public static void Init(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
        using var c = Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS logs(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    level TEXT NOT NULL,
                    source TEXT,
                    message TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_logs_ts ON logs(ts);
                CREATE TABLE IF NOT EXISTS ops(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    action TEXT NOT NULL,
                    detail TEXT,
                    monitor TEXT);
                CREATE INDEX IF NOT EXISTS ix_ops_ts ON ops(ts);
                """;
            cmd.ExecuteNonQuery();
        }
        Cleanup();
    }

    private static SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        return c;
    }

    // ---- 写入 ----

    public static void WriteLog(string ts, string level, string source, string message)
    {
        if (_connString is null) return;
        try
        {
            lock (Lock)
            using (var c = Open())
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO logs(ts,level,source,message) VALUES(@ts,@lv,@src,@msg)";
                cmd.Parameters.AddWithValue("@ts", ts);
                cmd.Parameters.AddWithValue("@lv", level);
                cmd.Parameters.AddWithValue("@src", source ?? "");
                cmd.Parameters.AddWithValue("@msg", message);
                cmd.ExecuteNonQuery();
            }
        }
        catch { /* 日志入库失败不能影响主流程 */ }
    }

    /// <summary>操作审计。kind: wallpaper/fence/icon/settings/process/screen；action: set/remove/create/delete/open/rename/move/restart…</summary>
    public static void Audit(string kind, string action, string detail, string? monitor = null)
    {
        if (_connString is null) return;
        try
        {
            lock (Lock)
            using (var c = Open())
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO ops(ts,kind,action,detail,monitor) VALUES(@ts,@k,@a,@d,@m)";
                cmd.Parameters.AddWithValue("@ts", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                cmd.Parameters.AddWithValue("@k", kind);
                cmd.Parameters.AddWithValue("@a", action);
                cmd.Parameters.AddWithValue("@d", detail ?? "");
                cmd.Parameters.AddWithValue("@m", monitor ?? "");
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            // 降级留痕：审计静默失败曾掩盖整段缺失（真机：ops 表 8 天零记录无任何痕迹），至少写文件日志
            try { Serilog.Log.Warning("ops 审计写入失败：{Kind}/{Action} {Err}", kind, action, ex.Message); } catch { }
        }
    }

    // ---- 查询（设置窗口日志页） ----

    /// <summary>合并查询：ops 记为 level='OPS'，logs 原级别；按时间倒序。days 天内 + 只取 levelMin 及以上。</summary>
    public static List<LogRow> Query(int days, string levelMin)
    {
        var result = new List<LogRow>();
        if (_connString is null) return result;
        var since = DateTime.Now.AddDays(-days).ToString("yyyy-MM-dd HH:mm:ss");
        var minRank = LevelRank(levelMin);
        try
        {
            lock (Lock)
            using (var c = Open())
            {
                string sql = $"""
                    SELECT id, ts, 'OPS' AS level, kind || '/' || action AS source,
                           detail || CASE WHEN monitor<>'' THEN ' ['||monitor||']' ELSE '' END AS message
                    FROM ops WHERE ts >= @since
                    UNION ALL
                    SELECT id, ts, level, source, message FROM logs WHERE ts >= @since
                    ORDER BY ts DESC LIMIT 2000
                    """;
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("@since", since);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var level = r.GetString(2);
                        if (LevelRank(level) < minRank) continue;
                        result.Add(new LogRow(r.GetInt64(0), r.GetString(1), level, r.GetString(3), r.GetString(4)));
                    }
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>导出为文本（诊断报告用）。返回行数组。</summary>
    public static List<string> Export(int days)
    {
        var lines = new List<string> { $"DesktopManager 日志导出 {DateTime.Now:yyyy-MM-dd HH:mm:ss}（近 {days} 天）", new string('=', 60) };
        lines.AddRange(Query(days, "DBG").Select(r => $"{r.Ts} [{r.Level,-3}] [{r.Source}] {r.Message}"));
        return lines;
    }

    public static void Clear()
    {
        if (_connString is null) return;
        lock (Lock)
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM logs; DELETE FROM ops;";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>滚动清理：30 天前删除，logs 超 2 万条截断旧的一半。</summary>
    private static void Cleanup()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-30).ToString("yyyy-MM-dd HH:mm:ss");
            lock (Lock)
            using (var c = Open())
            {
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM logs WHERE ts < @c; DELETE FROM ops WHERE ts < @c;";
                    cmd.Parameters.AddWithValue("@c", cutoff);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = """
                        DELETE FROM logs WHERE id <= (SELECT id FROM logs ORDER BY id DESC LIMIT 1 OFFSET 20000);
                        DELETE FROM ops WHERE id <= (SELECT id FROM ops ORDER BY id DESC LIMIT 20000);
                        """;
                    cmd.ExecuteNonQuery();
                }
            }
        }
        catch { }
    }

    private static int LevelRank(string level) => level switch
    {
        "OPS" => 5, "ERR" => 4, "WRN" => 3, "INF" => 2, "DBG" => 1, _ => 2,
    };
}
