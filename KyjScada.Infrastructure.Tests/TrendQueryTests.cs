using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Domain.History;
using KyjScada.Infrastructure.Data;
using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 历史趋势查询测试:验证界面趋势页依赖的查询接口。
/// 重点验证:时间区间过滤、按时间升序、limit 生效、未知变量返回空。
/// </summary>
public static class TrendQueryTests
{
    private static string DbPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "_work", "trend_" + name + ".db");

    public static void RunAll(Action<string, Action> run)
    {
        run("趋势查询:按时间区间过滤", () =>
        {
            var db = DbPath("range");
            if (File.Exists(db)) File.Delete(db);

            var repo = new HistoryRepository(db);
            repo.EnsureSchema();

            var svc = new ArchiveService(repo);
            var dev = MakeDevice("V1");
            svc.BuildFrom(dev);

            var t0 = new DateTime(2026, 11, 4, 10, 0, 0);

            // 写 6 个点,间隔 10 秒
            for (int i = 0; i < 6; i++)
            {
                svc.Archive(MakeSnap("V1", 10.0 + i, t0.AddSeconds(i * 10)), dev);
            }

            // 查中间 20 秒 → 应该只有 2~3 个点
            var mid = repo.Query("V1", t0.AddSeconds(10), t0.AddSeconds(30));

            TestHelpers.AssertTrue(mid.Count >= 2 && mid.Count <= 3, "中间区间应有 2~3 个点,实际 " + mid.Count);
        });

        run("趋势查询:结果按时间升序", () =>
        {
            var db = DbPath("order");
            if (File.Exists(db)) File.Delete(db);

            var repo = new HistoryRepository(db);
            var svc = new ArchiveService(repo);
            var dev = MakeDevice("V1");
            svc.BuildFrom(dev);

            var t0 = new DateTime(2026, 11, 4, 11, 0, 0);

            for (int i = 0; i < 5; i++)
                svc.Archive(MakeSnap("V1", i, t0.AddSeconds(i * 10)), dev);

            var found = repo.Query("V1", t0.AddMinutes(-1), t0.AddMinutes(5));

            TestHelpers.AssertEq(5, found.Count, "应有 5 条");

            for (int i = 1; i < found.Count; i++)
            {
                TestHelpers.AssertTrue(found[i].SampleTime > found[i - 1].SampleTime,
                    "第 " + i + " 条时间应大于前一条");
            }
        });

        run("趋势查询:limit 生效", () =>
        {
            var db = DbPath("limit");
            if (File.Exists(db)) File.Delete(db);

            var repo = new HistoryRepository(db);
            var svc = new ArchiveService(repo);
            var dev = MakeDevice("V1");
            svc.BuildFrom(dev);

            var t0 = new DateTime(2026, 11, 4, 12, 0, 0);

            for (int i = 0; i < 20; i++)
                svc.Archive(MakeSnap("V1", i, t0.AddSeconds(i * 10)), dev);

            var found = repo.Query("V1", t0.AddMinutes(-1), t0.AddMinutes(10), limit: 7);

            TestHelpers.AssertEq(7, found.Count, "limit=7 应返回 7 条");
        });

        run("趋势查询:未知变量返回空列表", () =>
        {
            var db = DbPath("unknown");
            if (File.Exists(db)) File.Delete(db);

            var repo = new HistoryRepository(db);
            repo.EnsureSchema();

            var found = repo.Query("NotExistVar", DateTime.Now.AddDays(-1), DateTime.Now.AddDays(1));

            TestHelpers.AssertEq(0, found.Count, "未知变量应返回空");
        });

        run("趋势查询:空时间区间返回空", () =>
        {
            var db = DbPath("empty");
            if (File.Exists(db)) File.Delete(db);

            var repo = new HistoryRepository(db);
            var svc = new ArchiveService(repo);
            var dev = MakeDevice("V1");
            svc.BuildFrom(dev);

            var t0 = new DateTime(2026, 11, 4, 13, 0, 0);
            svc.Archive(MakeSnap("V1", 1.0, t0), dev);

            // 查一个不相交的区间
            var found = repo.Query("V1", t0.AddHours(5), t0.AddHours(6));

            TestHelpers.AssertEq(0, found.Count, "不相交区间应返回空");
        });

        run("趋势查询:变量字典包含单位", () =>
        {
            var db = DbPath("dict");
            if (File.Exists(db)) File.Delete(db);

            var repo = new HistoryRepository(db);
            var svc = new ArchiveService(repo);
            var dev = MakeDevice("WithUnit");
            svc.BuildFrom(dev);

            svc.Archive(MakeSnap("WithUnit", 5.5, DateTime.Now), dev);

            var dict = repo.GetVarDict();
            var entry = dict.FirstOrDefault(x => x.VarName == "WithUnit");

            TestHelpers.AssertTrue(entry != null, "字典应有该变量");
            TestHelpers.AssertStr("m", entry!.Unit, "单位应为 m");
        });

        run("趋势查询:总行数统计正确", () =>
        {
            var db = DbPath("count");
            if (File.Exists(db)) File.Delete(db);

            var repo = new HistoryRepository(db);
            var svc = new ArchiveService(repo);
            var dev = MakeDevice("V1");
            svc.BuildFrom(dev);

            var t0 = new DateTime(2026, 11, 4, 14, 0, 0);

            for (int i = 0; i < 4; i++)
                svc.Archive(MakeSnap("V1", i, t0.AddSeconds(i * 10)), dev);

            TestHelpers.AssertEq(4, (int)repo.Count(), "总行数应为 4");
        });

        run("趋势查询:limit<=0 回落默认而不是返回空", () =>
        {
            var db = DbPath("limit0");
            if (File.Exists(db)) File.Delete(db);

            var repo = new HistoryRepository(db);
            var svc = new ArchiveService(repo);
            var dev = MakeDevice("V1");
            svc.BuildFrom(dev);

            var t0 = new DateTime(2026, 11, 4, 15, 0, 0);

            for (int i = 0; i < 3; i++)
                svc.Archive(MakeSnap("V1", i, t0.AddSeconds(i * 10)), dev);

            // SQLite 的 LIMIT 0 = 返回 0 行。调用方(含界面/MCP)传 0 时应当回落默认上限,
            // 否则"有数据"会被静默显示成"没数据"。
            TestHelpers.AssertEq(3, repo.Query("V1", t0.AddMinutes(-1), t0.AddMinutes(5), limit: 0).Count,
                "limit=0 应回落默认值");
            TestHelpers.AssertEq(3, repo.QueryAll(t0.AddMinutes(-1), t0.AddMinutes(5), limit: 0).Count,
                "QueryAll limit=0 同样回落");
        });

        run("变量字典:重复取建不抛唯一约束异常", () =>
        {
            var db = DbPath("upsert");
            if (File.Exists(db)) File.Delete(db);

            // 两个仓储实例指向同一个库 —— 相当于"归档线程"和"MCP/报表"各建一个仓储。
            // 老实现是"先 SELECT 再 INSERT",两边都查不到就都会 INSERT,
            // 后者撞 UNIQUE(VarName) 抛异常,那条采集路径的归档就此中断。
            var repoA = new HistoryRepository(db);
            var repoB = new HistoryRepository(db);

            var ids = new System.Collections.Concurrent.ConcurrentBag<int>();

            var t1 = new Thread(() => ids.Add(repoA.GetOrCreateVarId("Dup", "dev1", "m")));
            var t2 = new Thread(() => ids.Add(repoB.GetOrCreateVarId("Dup", "dev2", "m")));

            t1.Start();
            t2.Start();
            t1.Join();
            t2.Join();

            // 再来一次(缓存已命中)也不能变
            ids.Add(repoA.GetOrCreateVarId("Dup", "dev1", "m"));

            TestHelpers.AssertEq(3, ids.Count, "三次取建都应成功返回");
            TestHelpers.AssertEq(1, ids.Distinct().Count(), "同名变量必须始终是同一个 VarId");
            TestHelpers.AssertEq(1, repoA.GetVarDict().Count(x => x.VarName == "Dup"), "字典里不能出现两行");
        });
    }

    private static DeviceConfig MakeDevice(string varName)
    {
        var device = new DeviceConfig
        {
            DeviceName = "D1",
            ProtocolKey = "ModbusTCP",
            Connection = new ConnectionConfig { Kind = ConnectionKind.Tcp, IpAddress = "127.0.0.1", Port = 502 }
        };

        var group = new GroupConfig { GroupName = "G1", IsActive = true, Start = "1.400001", Length = 10 };
        group.VarList.Add(new VariableConfig
        {
            VarName = varName,
            VarType = "Float",
            VarAddress = "1.400001",
            Start = "1.400001",
            Unit = "m",
            ArchiveEnable = true
        });

        device.GroupList.Add(group);
        return device;
    }

    private static ValueSnapshot MakeSnap(string name, double value, DateTime at)
    {
        var dict = new Dictionary<string, TagValue>(StringComparer.OrdinalIgnoreCase)
        {
            [name] = new TagValue(name, value, TagQuality.Good, at)
        };

        return new ValueSnapshot(1, at, true, dict);
    }
}
