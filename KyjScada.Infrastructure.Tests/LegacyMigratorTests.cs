using KyjScada.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 老库宽表 → 长表迁移的验证。用真 sqlite 文件造出老库结构(含脏值),再读回核对。
///
/// 必须测的三件事:
///  1. 脏值(空串 / -- / abc)绝不能变成 0 —— 这是迁移最容易犯、后果最重的错;
///  2. 整个空行不产生任何记录(否则趋势图会出现"采到 0"的假谷);
///  3. 重复执行不重复插入(现场一定会有人跑第二次)。
/// </summary>
public static class LegacyMigratorTests
{
    public static void RunAll(Action<string, Action> run)
    {
        run("老库迁移:脏值跳过、空行不落库、重复执行不重复", () =>
        {
            var dir = Path.Combine(WorkDir(), "kyj_mig_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            var legacyPath = Path.Combine(dir, "old.db");
            var targetPath = Path.Combine(dir, "history.db");

            try
            {
                BuildLegacy(legacyPath);

                var migrator = new LegacyMigrator(legacyPath, targetPath) { DeviceName = "老站" };
                var report = migrator.Run();

                // 老库 5 行:
                //  1 行全空       → 0 个值,不落库
                //  1 行时间非法   → 整行跳过
                //  余 3 行有值    → 2 + 1 + 2 = 5 个有效值
                TestHelpers.AssertEq(5, report.LegacyRows, "老库行数");
                TestHelpers.AssertEq(3, report.MigratedRows, "有值的行数");
                TestHelpers.AssertEq(5, report.Values, "有效测点值");

                // LQT_Level:第1行 5.5,第2行 6.0,第5行 7.25 → 3 个
                var repo = new HistoryRepository(targetPath);
                var level = repo.Query("LQT_Level", new DateTime(2020, 1, 1), new DateTime(2030, 1, 1));

                TestHelpers.AssertEq(3, level.Count, "LQT_Level 点数");
                TestHelpers.AssertEq(0, (int)level.Count(r => r.Value == 0), "不应出现 0 值");

                var bools = repo.Query("TestRegBool1", new DateTime(2020, 1, 1), new DateTime(2030, 1, 1));

                TestHelpers.AssertEq(1, bools.Count, "TestRegBool1 只应有 1 个有效值");
                TestHelpers.AssertEq(1, (int)Math.Round(bools[0].Value), "布尔 1 应保留");

                // 第 5 行的 7.25 必须精确迁移,不能被四舍五入成 7
                TestHelpers.AssertTrue(level.Any(r => Math.Abs(r.Value - 7.25) < 1e-9), "小数值应保留");

                // 系统日志原样进 LegacySysLog
                TestHelpers.AssertEq(2, report.SysLogRows, "系统日志行数");
                TestHelpers.AssertEq(2, CountTable(targetPath, "LegacySysLog"), "LegacySysLog 行数");

                // 再跑一次:幂等,不应把行数翻倍
                var again = new LegacyMigrator(legacyPath, targetPath) { DeviceName = "老站" }.Run();

                TestHelpers.AssertEq(0, again.Values, "第二次迁移不应再写入任何新点");
                TestHelpers.AssertEq(report.Values, again.SkippedExisting, "第二次应把第一遍的点全部记为已存在");
                TestHelpers.AssertEq(3, repo.Query("LQT_Level", new DateTime(2020, 1, 1), new DateTime(2030, 1, 1)).Count,
                    "重复执行后 LQT_Level 仍应为 3 条");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        });

        run("老库迁移:库里没有宽表时不报错、不写脏数据", () =>
        {
            var dir = Path.Combine(WorkDir(), "kyj_mig_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            var legacyPath = Path.Combine(dir, "empty.db");
            var targetPath = Path.Combine(dir, "history.db");

            try
            {
                using (var conn = new SqliteConnection("Data Source=" + legacyPath))
                {
                    conn.Open();

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "CREATE TABLE SomethingElse (A TEXT)";
                    cmd.ExecuteNonQuery();
                }

                var report = new LegacyMigrator(legacyPath, targetPath).Run();

                TestHelpers.AssertEq(0, report.MigratedRows, "不应迁移任何行");
                TestHelpers.AssertTrue(report.Warnings.Count > 0, "应给出警告");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        });
    }

    /// <summary>造一个老库:宽表 + SysLog,含各种脏值。</summary>
    private static void BuildLegacy(string path)
    {
        using var conn = new SqliteConnection("Data Source=" + path);

        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "CREATE TABLE ActualData (" +
                "ID INTEGER PRIMARY KEY, InsertTime TEXT, " +
                "LQT_Level TEXT, LQT_InTemp TEXT, TestRegBool1 TEXT);" +

                "CREATE TABLE SysLog (Id INTEGER PRIMARY KEY, InsertTime TEXT, Note TEXT, " +
                "LogType TEXT, Operator TEXT, varName TEXT, AlarmSet TEXT, AlarmValue TEXT, AlarmType TEXT);";
            cmd.ExecuteNonQuery();
        }

        void Insert(string id, string time, string level, string temp, string? flag)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO ActualData VALUES ($i, $t, $l, $p, $f)";
            cmd.Parameters.AddWithValue("$i", int.Parse(id));
            cmd.Parameters.AddWithValue("$t", time);
            cmd.Parameters.AddWithValue("$l", level);
            cmd.Parameters.AddWithValue("$p", temp);

            // null 必须显式转成 DBNull,否则 sqlite 驱动报 "Value must be set"
            cmd.Parameters.AddWithValue("$f", flag is null ? DBNull.Value : flag);

            cmd.ExecuteNonQuery();
        }

        // 1:正常
        Insert("1", "2026-03-10 08:00:00", "5.5", "17.125", null);
        // 2:部分脏值(空串 + --)
        Insert("2", "2026-03-10 08:00:01", "6.0", "--", "");
        // 3:整行空串 —— 一个值都没有
        Insert("3", "2026-03-10 08:00:02", "", "", "");
        // 4:时间非法 —— 整行跳过
        Insert("4", "not-a-time", "8.8", "20", null);
        // 5:小数 + 布尔
        Insert("5", "2026-03-10 08:00:04", "7.25", "NaN", "1");

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO SysLog (Id, InsertTime, Note, LogType, Operator, varName, AlarmSet, AlarmValue, AlarmType) " +
                "VALUES (1, '2026-03-10 08:00:00', '登录成功', '操作日志', 'Admin', NULL, NULL, NULL, '信息')," +
                "(2, '2026-03-10 08:05:00', '下限报警', '系统报警', NULL, 'LQT_Level', '0', '5.5', '报警');";
            cmd.ExecuteNonQuery();
        }
    }

    private static int CountTable(string path, string table)
    {
        using var conn = new SqliteConnection("Data Source=" + path);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM " + table;

        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>测试工作目录。用程序目录下的 _work,不用系统 TEMP(现场机器上 TEMP 常被策略限制)。</summary>
    private static string WorkDir()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "_work");

        Directory.CreateDirectory(dir);

        return dir;
    }
}