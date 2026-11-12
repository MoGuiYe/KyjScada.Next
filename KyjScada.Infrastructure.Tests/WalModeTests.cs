using KyjScada.Infrastructure.Alarm;
using KyjScada.Infrastructure.Users;
using Microsoft.Data.Sqlite;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// WAL 启用的回归用例。
///
/// 背景(本轮回归):报警库 / 用户库把 <c>PRAGMA journal_mode=wal</c> 的**返回值丢掉了**,
/// 只 try/catch;而"建表成功"又被记进静态 SchemaReady,于是之后 EnsureSchema
/// 永远走 `if (已经建好) return;`。现场后果:
///   首次切 WAL 失败(三方同时开库撞库级独占锁)→ 该库在**本进程内永久**停在
///   delete 模式,不重试、不报错;之后采集线程写 + 界面/MCP 读开始报 SQLITE_BUSY。
/// 老版本每条连接都重切一次,反而能自愈 —— 所以这是回归,必须用测试钉死。
/// </summary>
public static class WalModeTests
{
    private static string DbPath (string name) =>
        Path.Combine (AppContext.BaseDirectory, "_work", "wal_" + name + ".db");

    /// <summary>直接问 SQLite 当前 journal_mode,不信仓储自己的说法。</summary>
    private static string ReadJournalMode (string dbPath)
    {
        using var conn = new SqliteConnection (new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString ());

        conn.Open ();

        using var cmd = conn.CreateCommand ();
        cmd.CommandText = "PRAGMA journal_mode;";

        return (cmd.ExecuteScalar () as string ?? "").ToLowerInvariant ();
    }

    public static void RunAll (Action<string, Action> run)
    {
        run ("WAL:报警库建表后必须真的切到 wal", () =>
        {
            var db = DbPath ("alarm");

            if (File.Exists (db)) File.Delete (db);

            new AlarmRepository (db).EnsureSchema ();

            // 只看"没抛异常"是不够的:库被占着时那句会"成功但没切过去"
            TestHelpers.AssertStr ("wal", ReadJournalMode (db), "报警库应处于 wal 模式");

            TestHelpers.AssertTrue (AlarmRepository.GetLastWalError (db) is null,
                "切成功之后不该留 WAL 错误: " + AlarmRepository.GetLastWalError (db));
        });

        run ("WAL:用户库建表后必须真的切到 wal", () =>
        {
            var db = DbPath ("users");

            if (File.Exists (db)) File.Delete (db);

            new UserRepository (db).EnsureSchema ();

            TestHelpers.AssertStr ("wal", ReadJournalMode (db), "用户库应处于 wal 模式");

            TestHelpers.AssertTrue (UserRepository.GetLastWalError (db) is null,
                "切成功之后不该留 WAL 错误: " + UserRepository.GetLastWalError (db));
        });

        run ("WAL:报警库切不成时必须报错并按退避重试", () =>
        {
            // 内存库**永远**切不到 WAL(SQLite 只会返回 memory),用它稳定复现
            // "首次切失败"那个现场:而且建表照样成功 —— 正好就是
            // "表已建好、WAL 没切上"这个让旧代码永久躺平的状态。
            var repo = new AlarmRepository (":memory:");

            repo.EnsureSchema ();

            var first = AlarmRepository.GetLastWalError (":memory:");

            TestHelpers.AssertTrue (first is not null, "切不成必须记下原因(旧代码这里是完全静默的)");
            TestHelpers.AssertTrue (first!.Contains ("第 1 次"), "应记下已尝试 1 次,实际: " + first);

            // 第一次失败后要等指数退避(2^1=2 秒)才允许重试
            Thread.Sleep (2200);

            repo.EnsureSchema ();

            var second = AlarmRepository.GetLastWalError (":memory:");

            TestHelpers.AssertTrue (second is not null && second.Contains ("第 2 次"),
                "表建好之后仍必须再进来重试 WAL;若还停在\"第 1 次\",说明退避形同虚设、"
                + "该库会永久停在 delete 模式(并发写开始报 SQLITE_BUSY)。实际: " + second);

            Console.WriteLine ("        " + second);
        });

        run ("WAL:用户库切不成时必须报错并按退避重试", () =>
        {
            var repo = new UserRepository (":memory:");

            repo.EnsureSchema ();

            var first = UserRepository.GetLastWalError (":memory:");

            TestHelpers.AssertTrue (first is not null, "切不成必须记下原因(旧代码这里是完全静默的)");
            TestHelpers.AssertTrue (first!.Contains ("第 1 次"), "应记下已尝试 1 次,实际: " + first);

            Thread.Sleep (2200);

            repo.EnsureSchema ();

            var second = UserRepository.GetLastWalError (":memory:");

            TestHelpers.AssertTrue (second is not null && second.Contains ("第 2 次"),
                "表建好之后仍必须再进来重试 WAL,实际: " + second);

            Console.WriteLine ("        " + second);
        });
    }
}
