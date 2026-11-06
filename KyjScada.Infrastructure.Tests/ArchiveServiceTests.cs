using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Domain.History;
using KyjScada.Infrastructure.Data;
using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 归档服务测试:清单编译、周期判断、只存有效值、落库正确性、保留期清理。
/// 用内存假仓储测服务逻辑,用真 SQLite 测端到端。
/// </summary>
public static class ArchiveServiceTests
{
    public static void RunAll(Action<string, Action> run)
    {
        RunBuild(run);
        RunShouldWrite(run);
        RunArchiveLogic(run);
        RunEndToEnd(run);
        RunPurge(run);
        RunRealConfig(run);
    }

    /// <summary>内存假仓储:记录收到的点,便于断言。</summary>
    private sealed class FakeRepo : IHistoryRepository
    {
        public List<HistoryPoint> Received = new();
        public int WriteCallCount;
        public Func<Exception?>? ThrowOnWrite;
        public int DeleteBeforeResult = 7;

        public int WritePoints(IReadOnlyList<HistoryPoint> points)
        {
            WriteCallCount++;

            var ex = ThrowOnWrite?.Invoke();
            if (ex is not null) throw ex;

            Received.AddRange(points);
            return points.Count;
        }

        public int DeleteBefore(DateTime cutoff) => DeleteBeforeResult;
    }

    private static DeviceConfig MakeDevice(params (string name, bool archive)[] vars)
    {
        var device = new DeviceConfig
        {
            DeviceName = "D1",
            ProtocolKey = "ModbusTCP",
            Connection = new ConnectionConfig { Kind = ConnectionKind.Tcp, IpAddress = "127.0.0.1", Port = 502 }
        };

        var group = new GroupConfig { GroupName = "G1", IsActive = true, Start = "1.400001", Length = 20 };

        foreach (var (name, archive) in vars)
        {
            group.VarList.Add(new VariableConfig
            {
                VarName = name,
                VarType = "Float",
                VarAddress = "1.400001",
                Start = "1.400001",
                Unit = "m",
                ArchiveEnable = archive
            });
        }

        device.GroupList.Add(group);
        return device;
    }

    private static ValueSnapshot MakeSnapshot(
        (string name, double? value, TagQuality quality)[] items, DateTime at)
    {
        var dict = new Dictionary<string, TagValue>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value, quality) in items)
        {
            dict[name] = new TagValue(name, value, quality, at);
        }

        return new ValueSnapshot(1, at, true, dict);
    }

    private static void RunBuild(Action<string, Action> run)
    {
        run("归档清单:只收 ArchiveEnable=true 的变量", () =>
        {
            var svc = new ArchiveService(new FakeRepo());
            svc.BuildFrom(MakeDevice(("V1", true), ("V2", false), ("V3", true)));

            TestHelpers.AssertEq(2, svc.ArchiveVarCount, "应只归档 2 个变量");
        });

        run("归档清单:全部关闭时归档清单为空", () =>
        {
            var svc = new ArchiveService(new FakeRepo());
            svc.BuildFrom(MakeDevice(("V1", false), ("V2", false)));

            TestHelpers.AssertEq(0, svc.ArchiveVarCount, "不应有归档变量");
        });
    }

    private static void RunShouldWrite(Action<string, Action> run)
    {
        run("归档周期:默认 10 秒", () =>
        {
            var svc = new ArchiveService(new FakeRepo());
            TestHelpers.AssertTrue(svc.Interval == TimeSpan.FromSeconds(10), "默认间隔应为 10 秒");
        });

        run("归档周期:未写库时立即可以写", () =>
        {
            var svc = new ArchiveService(new FakeRepo());
            TestHelpers.AssertTrue(svc.ShouldWrite(DateTime.Now), "首次应可写");
        });

        run("归档周期:距上次写入不足 10 秒不能写", () =>
        {
            var repo = new FakeRepo();
            var svc = new ArchiveService(repo);
            svc.BuildFrom(MakeDevice(("V1", true)));

            var t = new DateTime(2026, 11, 4, 10, 0, 0);

            svc.Archive(MakeSnapshot(new[] { ("V1", (double?)1.0, TagQuality.Good) }, t), MakeDevice(("V1", true)));

            TestHelpers.AssertTrue(!svc.ShouldWrite(t.AddSeconds(5)), "5 秒后不应可写");
            TestHelpers.AssertTrue(svc.ShouldWrite(t.AddSeconds(10)), "10 秒后应可写");
        });
    }

    private static void RunArchiveLogic(Action<string, Action> run)
    {
        run("归档:只写 ArchiveEnable 变量,忽略其他", () =>
        {
            var repo = new FakeRepo();
            var svc = new ArchiveService(repo);
            var dev = MakeDevice(("V1", true), ("V2", false));

            svc.BuildFrom(dev);

            var t = new DateTime(2026, 11, 4, 10, 0, 0);
            var snap = MakeSnapshot(new[]
            {
                ("V1", (double?)5.5, TagQuality.Good),
                ("V2", 9.9, TagQuality.Good)
            }, t);

            var n = svc.Archive(snap, dev);

            TestHelpers.AssertEq(1, n, "应只写 1 行");
            TestHelpers.AssertEq(1, repo.Received.Count, "仓储应收到 1 个点");
            TestHelpers.AssertStr("V1", repo.Received[0].VarName, "应写 V1");
        });

        run("归档:null 值不落库", () =>
        {
            var repo = new FakeRepo();
            var svc = new ArchiveService(repo);
            var dev = MakeDevice(("V1", true));

            svc.BuildFrom(dev);

            var t = new DateTime(2026, 11, 4, 10, 0, 0);
            var snap = MakeSnapshot(new[] { ("V1", (double?)null, TagQuality.NotCollected) }, t);

            var n = svc.Archive(snap, dev);

            TestHelpers.AssertEq(0, n, "null 值不应写入");
            TestHelpers.AssertEq(0, repo.Received.Count, "仓储不应收到点");
        });

        run("归档:Bad 质量不落库", () =>
        {
            var repo = new FakeRepo();
            var svc = new ArchiveService(repo);
            var dev = MakeDevice(("V1", true));

            svc.BuildFrom(dev);

            var t = new DateTime(2026, 11, 4, 10, 0, 0);
            var snap = MakeSnapshot(new[] { ("V1", (double?)5.5, TagQuality.Bad) }, t);

            TestHelpers.AssertEq(0, svc.Archive(snap, dev), "Bad 质量不应写入");
        });

        run("归档:NaN/Infinity 不落库", () =>
        {
            var repo = new FakeRepo();
            var svc = new ArchiveService(repo);
            var dev = MakeDevice(("V1", true), ("V2", true));

            svc.BuildFrom(dev);

            var t = new DateTime(2026, 11, 4, 10, 0, 0);
            var snap = MakeSnapshot(new[]
            {
                ("V1", (double?)double.NaN, TagQuality.Good),
                ("V2", (double?)double.PositiveInfinity, TagQuality.Good)
            }, t);

            TestHelpers.AssertEq(0, svc.Archive(snap, dev), "非有限值不应写入");
        });

        run("归档:写库异常不抛出,只记 LastError", () =>
        {
            var repo = new FakeRepo { ThrowOnWrite = () => new InvalidOperationException("磁盘满") };
            var svc = new ArchiveService(repo);
            var dev = MakeDevice(("V1", true));

            svc.BuildFrom(dev);

            var t = new DateTime(2026, 11, 4, 10, 0, 0);
            var n = svc.Archive(MakeSnapshot(new[] { ("V1", (double?)5.5, TagQuality.Good) }, t), dev);

            TestHelpers.AssertEq(0, n, "异常时应返回 0 而不是抛出");
            TestHelpers.AssertTrue(svc.LastError != null, "应记录错误");
        });

        run("归档:累计写入计数正确", () =>
        {
            var repo = new FakeRepo();
            var svc = new ArchiveService(repo);
            var dev = MakeDevice(("V1", true));

            svc.BuildFrom(dev);

            var t0 = new DateTime(2026, 11, 4, 10, 0, 0);

            svc.Archive(MakeSnapshot(new[] { ("V1", (double?)1.0, TagQuality.Good) }, t0), dev);
            svc.Archive(MakeSnapshot(new[] { ("V1", (double?)2.0, TagQuality.Good) }, t0.AddSeconds(10)), dev);

            TestHelpers.AssertEq(2, (int)svc.TotalWritten, "累计应写 2 行");
        });
    }

    /// <summary>端到端:每个用例一个独立 db 文件。SQLite 连接池会持有文件句柄,复用同一文件删不掉。</summary>
    private static void RunEndToEnd(Action<string, Action> run)
    {
        run("归档端到端:真 SQLite 写入并查回", () =>
        {
            var dbPath = Path.Combine(AppContext.BaseDirectory, "_work", "archive_e2e1.db");
            if (File.Exists(dbPath)) File.Delete(dbPath);

            var repo = new HistoryRepository(dbPath);
            repo.EnsureSchema();

            var svc = new ArchiveService(repo);
            var dev = MakeDevice(("LQT_Level", true), ("LQT_InTemp", true));
            svc.BuildFrom(dev);

            var t = new DateTime(2026, 11, 4, 10, 0, 0);

            var n = svc.Archive(MakeSnapshot(new[]
            {
                ("LQT_Level", (double?)5.5, TagQuality.Good),
                ("LQT_InTemp", (double?)17.0, TagQuality.Good)
            }, t), dev);

            TestHelpers.AssertEq(2, n, "应写入 2 行");

            var found = repo.Query("LQT_Level", t.AddMinutes(-1), t.AddMinutes(1));
            TestHelpers.AssertEq(1, found.Count, "应查到 1 条");
            TestHelpers.AssertTrue(Math.Abs(found[0].Value - 5.5) < 1e-9, "值应为 5.5");
        });

        run("归档端到端:3 批 10 秒间隔共写 6 行", () =>
        {
            var dbPath = Path.Combine(AppContext.BaseDirectory, "_work", "archive_e2e2.db");
            if (File.Exists(dbPath)) File.Delete(dbPath);

            var repo = new HistoryRepository(dbPath);
            repo.EnsureSchema();

            var svc = new ArchiveService(repo);
            var dev = MakeDevice(("V1", true), ("V2", true));
            svc.BuildFrom(dev);

            var t0 = new DateTime(2026, 11, 4, 11, 0, 0);

            for (int i = 0; i < 3; i++)
            {
                svc.Archive(MakeSnapshot(new[]
                {
                    ("V1", (double?)(1.0 + i), TagQuality.Good),
                    ("V2", (double?)(2.0 + i), TagQuality.Good)
                }, t0.AddSeconds(i * 10)), dev);
            }

            TestHelpers.AssertEq(6, (int)svc.TotalWritten, "应累计写 6 行");

            var v1 = repo.Query("V1", t0.AddMinutes(-1), t0.AddMinutes(5));
            TestHelpers.AssertEq(3, v1.Count, "V1 应有 3 条");
        });

        run("归档端到端:与采集快照格式兼容(Quality 映射)", () =>
        {
            var dbPath = Path.Combine(AppContext.BaseDirectory, "_work", "archive_e2e3.db");
            if (File.Exists(dbPath)) File.Delete(dbPath);

            var repo = new HistoryRepository(dbPath);
            var svc = new ArchiveService(repo);
            var dev = MakeDevice(("V1", true));
            svc.BuildFrom(dev);

            var t = new DateTime(2026, 11, 4, 12, 0, 0);

            svc.Archive(MakeSnapshot(new[] { ("V1", (double?)5.5, TagQuality.Uncertain) }, t), dev);

            var found = repo.Query("V1", t.AddMinutes(-1), t.AddMinutes(1));
            TestHelpers.AssertEq(1, found.Count, "可疑质量也应落库");
            TestHelpers.AssertEq(HistoryQuality.Uncertain, found[0].Quality, "质量应映射为 Uncertain");
        });
    }

    private static void RunPurge(Action<string, Action> run)
    {
        run("保留期清理:retentionDays<=0 不清理", () =>
        {
            var repo = new FakeRepo();
            var svc = new ArchiveService(repo);

            TestHelpers.AssertEq(0, svc.Purge(DateTime.Now, 0), "0 天应返回 0");
            TestHelpers.AssertEq(0, svc.Purge(DateTime.Now, -1), "负数应返回 0");
        });

        run("保留期清理:正数天数会调用删除", () =>
        {
            var repo = new FakeRepo { DeleteBeforeResult = 42 };
            var svc = new ArchiveService(repo);

            var n = svc.Purge(new DateTime(2026, 11, 4), 365);

            TestHelpers.AssertEq(42, n, "应返回删除行数");
            TestHelpers.AssertEq(42, svc.LastPurged, "LastPurged 应更新");
        });

        run("保留期清理:真库删除 2020 年数据", () =>
        {
            var dbPath = Path.Combine(AppContext.BaseDirectory, "_work", "archive_purge.db");
            if (File.Exists(dbPath)) File.Delete(dbPath);

            var repo = new HistoryRepository(dbPath);
            var svc = new ArchiveService(repo);
            var dev = MakeDevice(("OldVar", true));
            svc.BuildFrom(dev);

            var old = new DateTime(2020, 1, 1, 0, 0, 0);
            svc.Archive(MakeSnapshot(new[] { ("OldVar", (double?)1.0, TagQuality.Good) }, old), dev);

            var deleted = svc.Purge(new DateTime(2026, 11, 4), 365);

            TestHelpers.AssertTrue(deleted >= 1, "应删除 2020 年的数据,实际 " + deleted);
        });
    }

    private static void RunRealConfig(Action<string, Action> run)
    {
        run("现场配置:35 个变量启用归档", () =>
        {
            var path = FindRealSettings();
            if (path is null) return;

            var projects = KyjScada.Domain.Config.SettingsJsonCodec.LoadFile(path);
            var project = KyjScada.Domain.Config.SettingsJsonCodec.ActiveProject(projects);
            var dev = project.AllDevices().First();

            var svc = new ArchiveService(new FakeRepo());
            svc.BuildFrom(dev);

            Console.WriteLine("        归档变量 " + svc.ArchiveVarCount + " 个");

            TestHelpers.AssertEq(35, svc.ArchiveVarCount, "现场应有 35 个归档变量");
        });
    }


    /// <summary>现场校验:App 运行时写入的历史库能读出归档数据。</summary>
    public static void VerifyLiveHistoryDb()
    {
        var db = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "KyjScada.App", "bin", "Debug", "net8.0-windows", "data", "history.db"));

        if (!File.Exists(db)) { Console.WriteLine("历史库不存在: " + db); return; }

        var repo = new HistoryRepository(db);
        var total = repo.Count();
        Console.WriteLine("        历史库总行数: " + total);

        if (total == 0) return;

        var dict = repo.GetVarDict();
        Console.WriteLine("        变量字典条数: " + dict.Count);

        var t0 = DateTime.Now.AddHours(-2);
        var t1 = DateTime.Now.AddHours(1);
        var level = repo.Query("LQT_Level", t0, t1);
        Console.WriteLine("        LQT_Level 记录数: " + level.Count);
        if (level.Count > 0)
        {
            foreach (var r in level.Take(3))
                Console.WriteLine("          " + r.SampleTime.ToString("HH:mm:ss") + " -> " + r.Value.ToString("F2"));
        }

        // 聚合诊断:确认自动分辨率到底选了哪一档
        if (level.Count > 1)
        {
            var from = DateTime.Now.AddHours(-1);
            var to = DateTime.Now;

            var inWindow = level.Where(r => r.SampleTime >= from && r.SampleTime <= to).ToList();

            Console.WriteLine("        最近 1 小时原始点数: " + inWindow.Count);

            if (inWindow.Count > 1)
            {
                var median = KyjScada.Runtime.TrendAggregator.MedianInterval(inWindow);
                var auto = KyjScada.Runtime.TrendAggregator.ResolveAutoInterval(from, to, inWindow);

                Console.WriteLine("        中位采样间隔: " + median.ToString("F1") + " 秒");
                Console.WriteLine("        自动分辨率: " + auto.ToString("F0") + " 秒");

                var buckets = KyjScada.Runtime.TrendAggregator.Aggregate(
                    inWindow, from, to, auto, KyjScada.Runtime.TrendAggregate.Average);

                var nonEmpty = buckets.Count(b => !b.IsEmpty);
                Console.WriteLine("        桶总数 " + buckets.Count + " / 非空 " + nonEmpty);

                if (nonEmpty > 0)
                {
                    var first = buckets.First(b => !b.IsEmpty);
                    Console.WriteLine("        首个非空桶: " + first.Time.ToString("HH:mm:ss")
                        + " 值=" + first.Value.ToString("F2")
                        + " 包络[" + first.Min.ToString("F2") + "," + first.Max.ToString("F2") + "]"
                        + " 点数=" + first.Count);
                }
            }
        }
    }

    private static string? FindRealSettings()
    {
        var baseDir = AppContext.BaseDirectory;

        var probe = Path.Combine(baseDir, "_work");
        if (Directory.Exists(probe))
        {
            var hit = Directory.GetFiles(probe, "ModbusTCP.json");
            if (hit.Length > 0) return hit[0];
        }

        var appDir = Path.Combine(baseDir, "..", "..", "..", "..", "KyjScada.App", "bin", "Debug", "net8.0-windows", "settings");
        if (Directory.Exists(appDir))
        {
            var hit2 = Directory.GetFiles(appDir, "ModbusTCP.json");
            if (hit2.Length > 0) return hit2[0];
        }

        return null;
    }
}
