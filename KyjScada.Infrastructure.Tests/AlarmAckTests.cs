using KyjScada.Contracts;
using KyjScada.Infrastructure.Alarm;
using Microsoft.Data.Sqlite;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 报警确认(Ack)与老库迁移测试。
/// 重点:老库没有 AckAt 列时必须能自动补列,不能因为升级就丢历史报警。
/// </summary>
public static class AlarmAckTests
{
    private static string DbPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "_work", "alarm_" + name + ".db");

    private static AlarmEvent Trigger(string var, double v, DateTime at)
        => new(var, AlarmDirection.High, AlarmLevel.Normal, AlarmLevel.Active, v, at);

    public static void RunAll(Action<string, Action> run)
    {
        run("报警确认:新库默认未确认", () =>
        {
            var db = DbPath("ack1");
            if (File.Exists(db)) File.Delete(db);

            var repo = new AlarmRepository(db);
            repo.EnsureSchema();

            var t = new DateTime(2026, 11, 5, 8, 0, 0);
            repo.WriteEvents(new List<AlarmEvent> { Trigger("V1", 100, t) });

            var rows = repo.Query("V1");

            TestHelpers.AssertEq(1, rows.Count, "应有 1 条");
            TestHelpers.AssertTrue(!rows[0].IsAcked, "新事件应为未确认");
            TestHelpers.AssertStr("未确认", rows[0].AckText, "状态文本");
            TestHelpers.AssertTrue(rows[0].Id > 0, "应有 Id");
        });

        run("报警确认:确认单条后状态变已确认", () =>
        {
            var db = DbPath("ack2");
            if (File.Exists(db)) File.Delete(db);

            var repo = new AlarmRepository(db);
            repo.EnsureSchema();

            var t = new DateTime(2026, 11, 5, 9, 0, 0);
            repo.WriteEvents(new List<AlarmEvent> { Trigger("V1", 100, t) });

            var row = repo.Query("V1")[0];

            var n = repo.Acknowledge(row.Id, DateTime.Now);

            TestHelpers.AssertEq(1, n, "应更新 1 行");

            var after = repo.Query("V1")[0];

            TestHelpers.AssertTrue(after.IsAcked, "应变成已确认");
            TestHelpers.AssertStr("已确认", after.AckText, "状态文本");
        });

        run("报警确认:重复确认同一条返回 0(幂等)", () =>
        {
            var db = DbPath("ack3");
            if (File.Exists(db)) File.Delete(db);

            var repo = new AlarmRepository(db);
            repo.EnsureSchema();

            var t = new DateTime(2026, 11, 5, 10, 0, 0);
            repo.WriteEvents(new List<AlarmEvent> { Trigger("V1", 100, t) });

            var row = repo.Query("V1")[0];

            TestHelpers.AssertEq(1, repo.Acknowledge(row.Id, DateTime.Now), "第一次应成功");
            TestHelpers.AssertEq(0, repo.Acknowledge(row.Id, DateTime.Now), "第二次应返回 0");
        });

        run("报警确认:全部确认只影响未确认的", () =>
        {
            var db = DbPath("ack4");
            if (File.Exists(db)) File.Delete(db);

            var repo = new AlarmRepository(db);
            repo.EnsureSchema();

            var t = new DateTime(2026, 11, 5, 11, 0, 0);

            repo.WriteEvents(new List<AlarmEvent>
            {
                Trigger("V1", 100, t),
                Trigger("V2", 200, t.AddMinutes(1)),
                Trigger("V3", 300, t.AddMinutes(2))
            });

            // 先单独确认一条
            var first = repo.Query("V1")[0];
            repo.Acknowledge(first.Id, DateTime.Now);

            var n = repo.AcknowledgeAll(t.AddHours(1), DateTime.Now);

            TestHelpers.AssertEq(2, n, "应只确认剩下的 2 条");

            var all = repo.Query(null, null, null, 100);
            TestHelpers.AssertTrue(all.All(x => x.IsAcked), "全部应已确认");
        });

        run("报警确认:带 since 时只确认区间内(不能把历史全确认掉)", () =>
        {
            var db = DbPath("ack7");
            if (File.Exists(db)) File.Delete(db);

            var repo = new AlarmRepository(db);
            repo.EnsureSchema();

            var old = new DateTime(2020, 1, 1, 0, 0, 0);
            var recent = new DateTime(2026, 11, 5, 14, 0, 0);

            repo.WriteEvents(new List<AlarmEvent>
            {
                Trigger("OldVar", 1, old),
                Trigger("NewVar", 2, recent)
            });

            // 只确认最近 1 小时内的
            var n = repo.AcknowledgeAll(recent.AddMinutes(5), DateTime.Now, recent.AddHours(-1));

            TestHelpers.AssertEq(1, n, "应只确认 1 条,实际 " + n);

            var oldRow = repo.Query("OldVar")[0];
            var newRow = repo.Query("NewVar")[0];

            TestHelpers.AssertTrue(!oldRow.IsAcked, "2020 年的历史报警不应被确认");
            TestHelpers.AssertTrue(newRow.IsAcked, "最近的报警应被确认");
        });

        run("报警确认:按未确认过滤查询", () =>
        {
            var db = DbPath("ack5");
            if (File.Exists(db)) File.Delete(db);

            var repo = new AlarmRepository(db);
            repo.EnsureSchema();

            var t = new DateTime(2026, 11, 5, 12, 0, 0);

            repo.WriteEvents(new List<AlarmEvent>
            {
                Trigger("V1", 100, t),
                Trigger("V2", 200, t.AddMinutes(1))
            });

            var one = repo.Query("V1")[0];
            repo.Acknowledge(one.Id, DateTime.Now);

            var unacked = repo.Query(null, null, null, 100, acked: false);
            TestHelpers.AssertEq(1, unacked.Count, "未确认应剩 1 条");
            TestHelpers.AssertStr("V2", unacked[0].VarName, "未确认的应是 V2");

            var acked = repo.Query(null, null, null, 100, acked: true);
            TestHelpers.AssertEq(1, acked.Count, "已确认应有 1 条");
            TestHelpers.AssertStr("V1", acked[0].VarName, "已确认的应是 V1");
        });

        run("报警确认:老库无 AckAt 列能自动补列(升级不丢历史)", () =>
        {
            var db = DbPath("legacy");
            if (File.Exists(db)) File.Delete(db);

            // 手工建一个"旧版本"的表:没有 AckAt 列
            using (var conn = new SqliteConnection("Data Source=" + db))
            {
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
CREATE TABLE AlarmEvents (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    VarName     TEXT    NOT NULL,
    Direction   INTEGER NOT NULL,
    IsTrigger   INTEGER NOT NULL,
    Value       REAL    NOT NULL,
    OccurredAt  TEXT    NOT NULL,
    WrittenAt   TEXT    NOT NULL
);
INSERT INTO AlarmEvents (VarName, Direction, IsTrigger, Value, OccurredAt, WrittenAt)
VALUES ('OldVar', 0, 1, 42.0, '2026-11-01T10:00:00.0000000+08:00', '2026-11-01T10:00:00.0000000+08:00');";

                cmd.ExecuteNonQuery();
            }

            // 用新代码打开:EnsureSchema 应把 AckAt 列补上
            var repo = new AlarmRepository(db);
            repo.EnsureSchema();

            var rows = repo.Query("OldVar");

            TestHelpers.AssertEq(1, rows.Count, "老数据应仍在(升级不丢历史)");
            TestHelpers.AssertTrue(!rows[0].IsAcked, "老数据补列后应为未确认");

            // 补列后确认功能应可用
            var n = repo.Acknowledge(rows[0].Id, DateTime.Now);
            TestHelpers.AssertEq(1, n, "补列后应能确认");
        });

        run("报警确认:触发/消除类型文本", () =>
        {
            var db = DbPath("ack6");
            if (File.Exists(db)) File.Delete(db);

            var repo = new AlarmRepository(db);
            repo.EnsureSchema();

            var t = new DateTime(2026, 11, 5, 13, 0, 0);

            repo.WriteEvents(new List<AlarmEvent>
            {
                Trigger("V1", 100, t),
                new("V1", AlarmDirection.High, AlarmLevel.Active, AlarmLevel.Normal, 10, t.AddSeconds(10))
            });

            var rows = repo.Query("V1");

            // 倒序:消除在前
            TestHelpers.AssertStr("消除", rows[0].TypeText, "第一条应为消除");
            TestHelpers.AssertStr("触发", rows[1].TypeText, "第二条应为触发");
            TestHelpers.AssertStr("上限", rows[0].DirectionText, "方向应为上限");
        });
    }
}