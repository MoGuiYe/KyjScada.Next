using KyjScada.Domain.History;
using KyjScada.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 历史库建索引的回归用例。
///
/// 背景(本轮回归):建索引那段 DDL 是"见错就 return"的 —— 第一条索引一失败,
/// 后面两条**一条都不建**,而三条的顺序是"时间 → 变量+时间 → 变量+Id":
/// 越靠后越关键(idx_history_var_id 是"每变量最新值"用的,MCP read_variable /
/// 界面最新值全靠它,没有它那句 SQL 要扫全表再分组)。
/// 更糟的是失败之后 SchemaReady 照样标记"建好了",于是本进程内再也不会重试,
/// 现场只剩"重启碰运气"。
/// </summary>
public static class HistoryIndexTests
{
    private static string DbPath (string name) =>
        Path.Combine (AppContext.BaseDirectory, "_work", "history_" + name + ".db");

    private static void Exec (string dbPath, string sql)
    {
        using var conn = new SqliteConnection ("Data Source=" + dbPath);
        conn.Open ();

        using var cmd = conn.CreateCommand ();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery ();
    }

    private static bool IndexExists (string dbPath, string name)
    {
        using var conn = new SqliteConnection ("Data Source=" + dbPath);
        conn.Open ();

        using var cmd = conn.CreateCommand ();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=$n;";
        cmd.Parameters.AddWithValue ("$n", name);

        return Convert.ToInt64 (cmd.ExecuteScalar ()) > 0;
    }

    public static void RunAll (Action<string, Action> run)
    {
        run ("历史库建索引:第一条失败不能拖累后面两条(idx_history_var_id 必须建上)", () =>
        {
            var db = DbPath ("partial_index");

            if (File.Exists (db)) File.Delete (db);

            // 用一张**同名表**顶掉第一条索引名:CREATE INDEX idx_history_time 必然失败。
            // 旧实现遇到它就直接 return —— 后面两条(含关键的 idx_history_var_id)一条都不建。
            Exec (db, "CREATE TABLE " + HistorySchema.IndexByTime + " (x INTEGER);");

            new HistoryRepository (db).EnsureSchema ();

            TestHelpers.AssertTrue (HistoryRepository.GetLastIndexError (db) is not null,
                "第一条索引建不上必须被记下来(证明这条测试真的制造了失败)");

            TestHelpers.AssertTrue (!IndexExists (db, HistorySchema.IndexByTime),
                "第一条索引本来就该建不上(名字被占着)");

            TestHelpers.AssertTrue (IndexExists (db, HistorySchema.IndexByVarTime),
                "第一条失败之后,第二条索引仍必须尝试并建上");

            TestHelpers.AssertTrue (IndexExists (db, HistorySchema.IndexByVarId),
                "第一条失败之后,idx_history_var_id(每变量最新值的关键索引)也必须建上");

            // 三条 DDL 相互独立,失败的必须被**收齐**而不是见一条就放弃
            TestHelpers.AssertTrue (HistoryRepository.GetLastIndexError (db)!.Contains ("1 次建索引失败"),
                "错误里应带上重试记账: " + HistoryRepository.GetLastIndexError (db));

            Console.WriteLine ("        " + HistoryRepository.GetLastIndexError (db));
        });

        run ("历史库建索引:失败后表建好也要按退避再试(不能只剩重启一次机会)", () =>
        {
            var db = DbPath ("index_retry");

            if (File.Exists (db)) File.Delete (db);

            Exec (db, "CREATE TABLE " + HistorySchema.IndexByTime + " (x INTEGER);");

            var repo = new HistoryRepository (db);

            repo.EnsureSchema ();

            var first = HistoryRepository.GetLastIndexError (db);

            TestHelpers.AssertTrue (first is not null && first.Contains ("第 1 次"), "第一次失败应记下来: " + first);

            // 退避(2 秒)没到之前不许重试
            repo.EnsureSchema ();

            TestHelpers.AssertTrue (HistoryRepository.GetLastIndexError (db)!.Contains ("第 1 次"),
                "退避时间没到不该重试,实际: " + HistoryRepository.GetLastIndexError (db));

            Thread.Sleep (2200);

            repo.EnsureSchema ();

            TestHelpers.AssertTrue (HistoryRepository.GetLastIndexError (db)!.Contains ("第 2 次"),
                "表已建好之后仍必须再进来重试建索引,实际: " + HistoryRepository.GetLastIndexError (db));

            Console.WriteLine ("        " + HistoryRepository.GetLastIndexError (db));
        });
    }
}
