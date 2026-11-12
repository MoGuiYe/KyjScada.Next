using KyjScada.Contracts;
using KyjScada.Domain.History;
using KyjScada.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 批量归档(WritePoints)的变量名去重用例。
///
/// 背景:WritePoints 改成"先把缓存里没有的变量名去重凑成一批,一个连接一次解决"
/// (早先是逐点 GetOrCreateVarId:首次归档 35 个变量就是 35 次 OpenConnection)。
/// 去重这一层如果写漏,同一批里的同名变量会各自建一个 VarId ——
/// 同一个物理量被拆成两条曲线,MCP 的"最新值"也只回其中一条。
/// </summary>
public static class HistoryBatchTests
{
    private static string DbPath (string name) =>
        Path.Combine (AppContext.BaseDirectory, "_work", "history_" + name + ".db");

    private static int Scalar (string dbPath, string sql)
    {
        using var conn = new SqliteConnection ("Data Source=" + dbPath);
        conn.Open ();

        using var cmd = conn.CreateCommand ();
        cmd.CommandText = sql;

        return Convert.ToInt32 (cmd.ExecuteScalar ());
    }

    private static HistoryPoint Pt (DateTime at, string name, double v) =>
        new (at, name, "kongyaji", "m", v, (int)HistoryQuality.Good);

    public static void RunAll (Action<string, Action> run)
    {
        run ("历史归档:一批里的重名变量(含大小写不同)只能建一个 VarId", () =>
        {
            var db = DbPath ("batch_varid");

            if (File.Exists (db)) File.Delete (db);

            var repo = new HistoryRepository (db);
            var t0 = new DateTime (2026, 11, 11, 8, 0, 0);

            // 同一批里三个写法(只有大小写不同)必须当成同一个变量
            var written = repo.WritePoints (new List<HistoryPoint>
            {
                Pt (t0, "LQT_Level", 1.0),
                Pt (t0.AddSeconds (1), "lqt_level", 2.0),
                Pt (t0.AddSeconds (2), "LQT_LEVEL", 3.0),
                Pt (t0, "LQT_InTemp", 4.0)
            });

            TestHelpers.AssertEq (4, written, "4 个点都应写入");
            TestHelpers.AssertEq (2, Scalar (db, "SELECT COUNT(*) FROM VarDict;"),
                "重名变量只该建 1 行 VarDict(加上另一个变量共 2 行)");

            // 三条历史必须落在同一个 VarId 上,否则同一物理量被拆成三条曲线
            TestHelpers.AssertEq (1, Scalar (db,
                "SELECT COUNT(DISTINCT h.VarId) FROM History h " +
                "JOIN VarDict d ON d.VarId = h.VarId WHERE upper(d.VarName) = 'LQT_LEVEL';"),
                "大小写不同的同名点必须共用一个 VarId");

            TestHelpers.AssertEq (3, Scalar (db,
                "SELECT COUNT(*) FROM History h JOIN VarDict d ON d.VarId = h.VarId " +
                "WHERE upper(d.VarName) = 'LQT_LEVEL';"),
                "三个点都该写进去(去重只影响 VarId,不能吞数据)");
        });

        run ("历史归档:同一批里 35 个变量各建各的 VarId,重复批次复用", () =>
        {
            var db = DbPath ("batch_many");

            if (File.Exists (db)) File.Delete (db);

            var repo = new HistoryRepository (db);
            var t0 = new DateTime (2026, 11, 11, 9, 0, 0);

            var names = Enumerable.Range (0, 35).Select (i => "VAR_" + i).ToList ();

            var first = repo.WritePoints (names.Select (n => Pt (t0, n, 1.0)).ToList ());

            TestHelpers.AssertEq (35, first, "首批 35 个变量都该写入");
            TestHelpers.AssertEq (35, Scalar (db, "SELECT COUNT(*) FROM VarDict;"), "应建 35 个 VarId");

            // 第二批同名:必须复用(VarId 不再新增),否则 VarDict 会被写爆
            var second = repo.WritePoints (names.Select (n => Pt (t0.AddSeconds (10), n, 2.0)).ToList ());

            TestHelpers.AssertEq (35, second, "第二批也该写入");
            TestHelpers.AssertEq (35, Scalar (db, "SELECT COUNT(*) FROM VarDict;"),
                "同名变量必须复用 VarId,不能重复建");
            TestHelpers.AssertEq (70, Scalar (db, "SELECT COUNT(*) FROM History;"), "两批都该在库里");
        });
    }
}
