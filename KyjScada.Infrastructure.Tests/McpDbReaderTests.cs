using KyjScada.Contracts;
using KyjScada.Infrastructure.Alarm;
using KyjScada.Infrastructure.Data;
using KyjScada.Mcp.Data;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// MCP 的只读数据层(DbReader)测试。
///
/// 背景:现场发现 query_alarms 一直返回 0 条,而界面上明明有 8 条未确认报警。
/// 原因是新架构把报警写在**同目录的 alarms.db**,历史库 history.db 里根本没有 AlarmEvents 表;
/// 早期 DbReader 只查 history.db,所以永远查不到。
///
/// 这里锁死三件事:
///  1. 报警在旁路库(alarms.db)时也能查到,且时间统一成 yyyy-MM-dd HH:mm:ss;
///  2. 两个库都没有报警时返回空表而不是抛异常(MCP 工具不能崩);
///  3. 时间范围过滤生效,范围外的报警不会被算进来。
/// </summary>
public static class McpDbReaderTests
{
    public static void RunAll(Action<string, Action> run)
    {
        run("MCP 数据层:报警在旁路库 alarms.db 里也能查(时间已规整)", () =>
        {
            var dir = Path.Combine(WorkDir(), "mcp_db_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                var historyPath = Path.Combine(dir, "history.db");
                var alarmPath = Path.Combine(dir, "alarms.db");

                // 历史库:只有长表,没有 AlarmEvents(现场就是这种结构)
                new HistoryRepository(historyPath).EnsureSchema();

                var alarmRepo = new AlarmRepository(alarmPath);
                alarmRepo.EnsureSchema();

                var t1 = new DateTime(2026, 11, 6, 13, 9, 15);
                var t2 = new DateTime(2026, 11, 6, 13, 19, 45);

                alarmRepo.WriteEvents(new[]
                {
                    new AlarmEvent("LQT_Level", AlarmDirection.High, AlarmLevel.Normal, AlarmLevel.Active, 55, t1),
                    new AlarmEvent("LQT_InTemp", AlarmDirection.High, AlarmLevel.Normal, AlarmLevel.Active, 63.5, t2),
                });

                var db = new DbReader(historyPath);

                TestHelpers.AssertTrue(db.IsNewSchema, "应识别为长表库");

                var rows = db.QueryLogs(new DateTime(2026, 11, 6), new DateTime(2026, 11, 7));

                TestHelpers.AssertEq(2, rows.Count, "报警行数");

                // 时间必须是库里统一的空格格式,不能把 "o"(带 T)直接透给用户
                TestHelpers.AssertTrue(rows.All(r => r.InsertTime.Length == 19 && !r.InsertTime.Contains('T')),
                    "报警时间应规整为 yyyy-MM-dd HH:mm:ss");

                TestHelpers.AssertTrue(rows.Any(r => r.InsertTime == "2026-11-06 13:19:45"
                    && r.VarName == "LQT_InTemp" && r.LogType == "报警"),
                    "13:19:45 的 LQT_InTemp 触发事件应能查到");

                // 库概况里的"报警/日志行数"也要把 alarms.db 算进去
                var summary = db.GetSummary();
                TestHelpers.AssertEq(2, (int)summary.SysLogRows, "库概况报警行数");
            }
            finally
            {
                TryDelete(dir);
            }
        });

        run("MCP 数据层:没有报警库时返回空表,不抛异常", () =>
        {
            var dir = Path.Combine(WorkDir(), "mcp_db_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                var historyPath = Path.Combine(dir, "history.db");
                new HistoryRepository(historyPath).EnsureSchema();

                var db = new DbReader(historyPath);

                var rows = db.QueryLogs(new DateTime(2026, 11, 6), new DateTime(2026, 11, 7));

                TestHelpers.AssertEq(0, rows.Count, "无报警库时应返回 0 行");
                TestHelpers.AssertEq(0, (int)db.GetSummary().SysLogRows, "无报警库时行数为 0");
            }
            finally
            {
                TryDelete(dir);
            }
        });

        run("MCP 数据层:查询窗口按时间过滤,不把范围外的报警算进来", () =>
        {
            var dir = Path.Combine(WorkDir(), "mcp_db_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                var historyPath = Path.Combine(dir, "history.db");
                var alarmPath = Path.Combine(dir, "alarms.db");

                new HistoryRepository(historyPath).EnsureSchema();

                var alarmRepo = new AlarmRepository(alarmPath);
                alarmRepo.EnsureSchema();

                alarmRepo.WriteEvents(new[]
                {
                    new AlarmEvent("旧报警", AlarmDirection.High, AlarmLevel.Normal, AlarmLevel.Active, 1,
                        new DateTime(2026, 11, 1, 0, 0, 0)),
                    new AlarmEvent("新报警", AlarmDirection.Low, AlarmLevel.Normal, AlarmLevel.Active, 2,
                        new DateTime(2026, 11, 6, 8, 0, 0)),
                });

                var db = new DbReader(historyPath);

                var rows = db.QueryLogs(new DateTime(2026, 11, 6), new DateTime(2026, 11, 7));

                TestHelpers.AssertEq(1, rows.Count, "只应查到 11-06 当天的 1 条");
                TestHelpers.AssertStr("新报警", rows[0].VarName, "查到的应是当天那条");
                TestHelpers.AssertStr("低限", rows[0].AlarmType, "方向应映射为低限");
            }
            finally
            {
                TryDelete(dir);
            }
        });
    }

    private static string WorkDir()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "_work");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch
        {
            // sqlite 连接池可能还握着文件句柄,清理失败不影响测试结论
        }
    }
}