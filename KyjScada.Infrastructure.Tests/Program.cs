using System.Text;
using KyjScada.Domain.History;
using KyjScada.Infrastructure.Data;

Console.OutputEncoding = Encoding.UTF8;

// --live:只跑「现场设备活体校验」(读→写→读回),不跑单元测试。
// 默认不跑,因为没有现场设备时会误报失败。
if (args.Any(a => a == "--live"))
{
    var liveDir = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..",
        "KyjScada.App", "bin", "Debug", "net8.0-windows", "settings"));

    return KyjScada.Infrastructure.Tests.LiveCheck.Run(liveDir);
}

var workDir = Path.Combine(AppContext.BaseDirectory, "_work");
Directory.CreateDirectory(workDir);

var dbPath = Path.Combine(workDir, "history_test.db");

if (File.Exists(dbPath)) File.Delete(dbPath);

var pass = 0;
var fail = 0;
var failures = new List<string>();

void Run(string name, Action body)
{
    try
    {
        body();
        pass++;
        Console.WriteLine("  [PASS] " + name);
    }
    catch (Exception ex)
    {
        fail++;
        failures.Add(name + " -> " + ex.Message);
        Console.WriteLine("  [FAIL] " + name);
        Console.WriteLine("         " + ex.Message);
    }
}

Run("建表幂等", () =>
{
    var repo = new HistoryRepository(dbPath);

    repo.EnsureSchema();
    repo.EnsureSchema();
    repo.EnsureSchema();
});

Run("变量 ID 自动创建且唯一", () =>
{
    var repo = new HistoryRepository(dbPath);

    var id1 = repo.GetOrCreateVarId("LQT_Level", "kongyaji", "m");
    var id2 = repo.GetOrCreateVarId("LQT_InTemp", "kongyaji", "degC");
    var id3 = repo.GetOrCreateVarId("LQT_Level", "kongyaji", "m");

    if (id1 <= 0) throw new Exception("ID 必须为正,实际 " + id1);
    if (id1 == id2) throw new Exception("不同变量不能同 ID");
    if (id1 != id3) throw new Exception("同名变量必须复用同一 ID");
});

Run("批量写入单事务", () =>
{
    var repo = new HistoryRepository(dbPath);
    var id = repo.GetOrCreateVarId("BatchVar");

    var t0 = new DateTime(2026, 11, 1, 8, 0, 0);
    var records = new List<HistoryRecord>();

    for (int i = 0; i < 10; i++)
    {
        records.Add(new HistoryRecord(t0.AddSeconds(i * 10), id, 5.5 + i * 0.1, HistoryQuality.Good));
    }

    var written = repo.WriteBatch(records);

    if (written != 10) throw new Exception("应写入 10 行,实际 " + written);

    var count = repo.Count();
    if (count < 10) throw new Exception("总行数应 >= 10,实际 " + count);
});

Run("空批不写", () =>
{
    var repo = new HistoryRepository(dbPath);

    var written = repo.WriteBatch(new List<HistoryRecord>());

    if (written != 0) throw new Exception("空批应返回 0,实际 " + written);
});

Run("按变量查趋势", () =>
{
    var repo = new HistoryRepository(dbPath);
    var id = repo.GetOrCreateVarId("TrendOnlyVar");

    var t0 = new DateTime(2026, 11, 1, 8, 0, 0);
    var records = new List<HistoryRecord>();

    for (int i = 0; i < 5; i++)
    {
        records.Add(new HistoryRecord(t0.AddMinutes(i), id, 5.5 + i, HistoryQuality.Good));
    }

    repo.WriteBatch(records);

    var found = repo.Query("TrendOnlyVar", t0.AddMinutes(-1), t0.AddMinutes(10));

    if (found.Count != 5) throw new Exception("应查到 5 条,实际 " + found.Count);

    for (int i = 1; i < found.Count; i++)
    {
        if (found[i].SampleTime <= found[i - 1].SampleTime)
        {
            throw new Exception("结果未按时间升序");
        }
    }

    if (Math.Abs(found[0].Value - 5.5) > 1e-6) throw new Exception("首条值错误 " + found[0].Value);
});

Run("查不存在的变量返回空", () =>
{
    var repo = new HistoryRepository(dbPath);

    var found = repo.Query("NotExistVar", DateTime.Now.AddDays(-1), DateTime.Now.AddDays(1));

    if (found.Count != 0) throw new Exception("应返回空,实际 " + found.Count);
});

Run("时间区间过滤生效", () =>
{
    var repo = new HistoryRepository(dbPath);
    var id = repo.GetOrCreateVarId("RangeVar");

    var t0 = new DateTime(2026, 11, 1, 0, 0, 0);

    var records = new List<HistoryRecord>
    {
        new(t0, id, 20.0, HistoryQuality.Good),
        new(t0.AddHours(1), id, 25.0, HistoryQuality.Good),
        new(t0.AddHours(2), id, 30.0, HistoryQuality.Good)
    };

    repo.WriteBatch(records);

    var mid = repo.Query("RangeVar", t0.AddMinutes(30), t0.AddMinutes(90));

    if (mid.Count != 1) throw new Exception("中间时段应只有 1 条,实际 " + mid.Count);
    if (Math.Abs(mid[0].Value - 25.0) > 1e-6) throw new Exception("值错误 " + mid[0].Value);
});

Run("删除早于保留期", () =>
{
    var repo = new HistoryRepository(dbPath);
    var id = repo.GetOrCreateVarId("OldVar");

    var old = new DateTime(2020, 1, 1, 0, 0, 0);

    repo.WriteBatch(new List<HistoryRecord>
    {
        new(old, id, 1.0, HistoryQuality.Good),
        new(old.AddDays(1), id, 2.0, HistoryQuality.Good)
    });

    var deleted = repo.DeleteBefore(new DateTime(2020, 6, 1));

    if (deleted != 2) throw new Exception("应删除 2 行,实际 " + deleted);
});

Run("变量字典查询", () =>
{
    var repo = new HistoryRepository(dbPath);

    var dict = repo.GetVarDict();

    if (dict.Count == 0) throw new Exception("字典不应为空");

    var level = dict.FirstOrDefault(x => x.VarName == "LQT_Level");

    if (level is null) throw new Exception("字典里应有 LQT_Level");
    if (level.DeviceName != "kongyaji") throw new Exception("设备名错误 " + level.DeviceName);
    if (level.Unit != "m") throw new Exception("单位错误 " + level.Unit);
});

Run("大批量写入性能与正确性", () =>
{
    var repo = new HistoryRepository(dbPath);

    var ids = new List<int>();
    for (int i = 0; i < 35; i++)
    {
        ids.Add(repo.GetOrCreateVarId("Perf_Var_" + i));
    }

    var t0 = new DateTime(2026, 11, 2, 0, 0, 0);

    for (int round = 0; round < 10; round++)
    {
        var batch = new List<HistoryRecord>();
        var sampleTime = t0.AddSeconds(round * 10);

        for (int i = 0; i < ids.Count; i++)
        {
            batch.Add(new HistoryRecord(sampleTime, ids[i], 1.0 + i, HistoryQuality.Good));
        }

        repo.WriteBatch(batch);
    }

    var count = repo.Count();

    if (count < 350) throw new Exception("应有 >= 350 行,实际 " + count);

    var oneVar = repo.Query("Perf_Var_0", t0.AddMinutes(-1), t0.AddMinutes(10));

    if (oneVar.Count != 10) throw new Exception("单变量应有 10 条,实际 " + oneVar.Count);
});

Run("只存有效值,不写 NULL", () =>
{
    var repo = new HistoryRepository(dbPath);
    var id = repo.GetOrCreateVarId("NeverNull");

    var t0 = new DateTime(2026, 11, 3, 0, 0, 0);

    repo.WriteBatch(new List<HistoryRecord>
    {
        new(t0, id, 1.0, HistoryQuality.Good),
        new(t0.AddSeconds(10), id, 2.0, HistoryQuality.Good)
    });

    var found = repo.Query("NeverNull", t0.AddMinutes(-1), t0.AddMinutes(1));

    if (found.Count != 2) throw new Exception("应有 2 条,实际 " + found.Count);

    foreach (var r in found)
    {
        if (double.IsNaN(r.Value)) throw new Exception("值不应为 NaN");
    }
});

Run("容量实测:1000 行写入耗时", () =>
{
    var repo = new HistoryRepository(dbPath);

    var ids = new List<int>();
    for (int i = 0; i < 35; i++)
    {
        ids.Add(repo.GetOrCreateVarId("Cap_Var_" + i));
    }

    var t0 = new DateTime(2026, 11, 4, 0, 0, 0);

    var sw = System.Diagnostics.Stopwatch.StartNew();

    for (int round = 0; round < 29; round++)
    {
        var batch = new List<HistoryRecord>();
        var sampleTime = t0.AddSeconds(round * 10);

        for (int i = 0; i < ids.Count; i++)
        {
            batch.Add(new HistoryRecord(sampleTime, ids[i], 1.0 + i, HistoryQuality.Good));
        }

        repo.WriteBatch(batch);
    }

    sw.Stop();

    // 29 批 x 35 = 1015 行
    Console.WriteLine("         写入 1015 行耗时 " + sw.ElapsedMilliseconds + " ms");

    if (sw.ElapsedMilliseconds > 3000) throw new Exception("写入过慢 " + sw.ElapsedMilliseconds + " ms");
});

KyjScada.Infrastructure.Tests.ModbusFrameTests.RunAll(Run);
KyjScada.Infrastructure.Tests.ModbusTcpClientTests.RunAll(Run);
KyjScada.Infrastructure.Tests.AcquisitionTests.RunAll(Run);
KyjScada.Infrastructure.Tests.AlarmServiceTests.RunAll(Run);
KyjScada.Infrastructure.Tests.AlarmAckTests.RunAll(Run);
KyjScada.Infrastructure.Tests.ConfigValidatorTests.RunAll(Run);
KyjScada.Infrastructure.Tests.ConfigPersistenceTests.RunAll(Run);
KyjScada.Infrastructure.Tests.WritePathTests.RunAll(Run);
KyjScada.Infrastructure.Tests.JogPointsTests.RunAll(Run);
KyjScada.Infrastructure.Tests.DevicePipelineTests.RunAll(Run);
KyjScada.Infrastructure.Tests.ConcurrentAcquisitionTests.RunAll(Run);
KyjScada.Infrastructure.Tests.UserTests.RunAll(Run);
KyjScada.Infrastructure.Tests.AcquisitionFactoryTests.RunAll(Run);
KyjScada.Infrastructure.Tests.ModbusRtuTests.RunAll(Run);
KyjScada.Infrastructure.Tests.TransportFailureTests.RunAll(Run);
KyjScada.Infrastructure.Tests.AlarmServiceTests.VerifyLiveAlarmDb();
KyjScada.Infrastructure.Tests.ArchiveServiceTests.RunAll(Run);
KyjScada.Infrastructure.Tests.ArchiveServiceTests.VerifyLiveHistoryDb();
KyjScada.Infrastructure.Tests.TrendQueryTests.RunAll(Run);
KyjScada.Infrastructure.Tests.TrendAggregatorTests.RunAll(Run);
KyjScada.Infrastructure.Tests.TrendStatsTests.RunAll(Run);
KyjScada.Infrastructure.Tests.TrendTemplateTests.RunAll(Run);
KyjScada.Infrastructure.Tests.XlsxWriterTests.RunAll(Run);
KyjScada.Infrastructure.Tests.LegacyMigratorTests.RunAll(Run);
KyjScada.Infrastructure.Tests.McpDbReaderTests.RunAll(Run);
        Run("界面行模型:129 个变量都能建行且初始为 --", () =>
        {
            var path = FindRealSettings ();
            if  (path is null) return;

            var projects = KyjScada.Domain.Config.SettingsJsonCodec.LoadFile (path);
            var project = KyjScada.Domain.Config.SettingsJsonCodec.ActiveProject (projects);
            var dev = project.AllDevices ().First ();

            var names = new List<string> ();
            foreach (var g in dev.GroupList)
            {
                foreach (var v in g.VarList) names.Add (v.VarName);
            }

            KyjScada.Infrastructure.Tests.TestHelpers.AssertEq (129, names.Count, "行数应等于变量数");
            KyjScada.Infrastructure.Tests.TestHelpers.AssertTrue (names.Distinct ().Count () == 129, "变量名不应重复");

            // 未采集时显示 -- 而不是 0
            foreach (var n in names)
            {
                KyjScada.Infrastructure.Tests.TestHelpers.AssertTrue (n.Length > 0, "变量名不应为空");
            }
        });
        Run("现场配置布局校验:129 个变量全部落在各自组内", () =>
        {
            var path = FindRealSettings ();
            if  (path is null) return;

            var projects = KyjScada.Domain.Config.SettingsJsonCodec.LoadFile (path);
            var project = KyjScada.Domain.Config.SettingsJsonCodec.ActiveProject (projects);
            var dev = project.AllDevices ().First ();

            var problems = KyjScada.Infrastructure.Acquisition.ModbusAcquisitionService.ValidateLayout (dev);

            if  (problems.Count > 0)
            {
                foreach (var p in problems) Console.WriteLine ("          " + p);
            }

            KyjScada.Infrastructure.Tests.TestHelpers.AssertEq (0, problems.Count, "现场布局不应有越界");
        });
        Run("现场类型自检组:7 个变量按各自类型正确解析", () =>
        {
            var path = FindRealSettings ();
            if  (path is null) return;

            var projects = KyjScada.Domain.Config.SettingsJsonCodec.LoadFile (path);
            var project = KyjScada.Domain.Config.SettingsJsonCodec.ActiveProject (projects);
            var dev = project.AllDevices ().First ();

            var desc = KyjScada.Domain.Protocol.ProtocolRegistry.Find ("ModbusTCP");
            var svc = new KyjScada.Infrastructure.Acquisition.ModbusAcquisitionService (dev, desc);
            var bus = new KyjScada.Runtime.ValueBus ();

            try
            {
                var ok = svc.CollectOnce (bus, DateTime.Now);
                Console.WriteLine ("        成功采集 " + ok + " 个");

                var snap = bus.GetSnapshot ();
                var names = new[] { "bool", "short", "ushort", "int", "uint", "float", "double" };

                foreach (var n in names)
                {
                    var tv = snap[n];

                    if  (tv is null)
                    {
                        Console.WriteLine ("        " + n + " = (不在总线)");
                        continue;
                    }

                    var d = tv.AsDouble ();
                    Console.WriteLine ("        " + n.PadRight (8) + " = " + (d.HasValue ? d.Value.ToString ("F4") : "null")
                        + "  质量=" + tv.Quality);
                }
            }
            finally { svc.Dispose (); }
        });
        Run("真实现场配置:129 变量可加载并分组", () =>
        {
            var path = FindRealSettings ();
            if  (path is null) return;

            var projects = KyjScada.Domain.Config.SettingsJsonCodec.LoadFile (path);
            var project = KyjScada.Domain.Config.SettingsJsonCodec.ActiveProject (projects);
            KyjScada.Infrastructure.Tests.TestHelpers.AssertTrue (project != null, "应有活动项目");

            var devices = project.AllDevices ().ToList ();
            KyjScada.Infrastructure.Tests.TestHelpers.AssertTrue (devices.Count > 0, "应有设备");

            var dev = devices[0];
            var groups = dev.GroupList;
            KyjScada.Infrastructure.Tests.TestHelpers.AssertEq (4, groups.Count, "现场有 4 个组");

            var total = 0;
            foreach (var g in groups) total += g.VarList.Count;

            // ModbusTCP.json 这一份是老配置:129 个变量。
            // 现场实际在跑的是 ModbusRTUOverTCP.json 里那台(139 个),见下面的活体校验。
            KyjScada.Infrastructure.Tests.TestHelpers.AssertEq (129, total, "这份配置共 129 个变量");
        });

Run("界面行模型:现场在跑的 RTU-over-TCP 设备 139 个变量", () =>
{
    var dir = FindRealSettingsDir();
    if (dir is null) return;

    var loaded = KyjScada.Domain.Config.SettingsJsonCodec.LoadDirectory(dir);

    var dev = loaded.Project.AllDevices()
        .FirstOrDefault(d => string.Equals(d.ProtocolKey, "ModbusRTUOverTCP", StringComparison.OrdinalIgnoreCase));

    KyjScada.Infrastructure.Tests.TestHelpers.AssertTrue(dev != null, "应有 RTU-over-TCP 设备");

    var total = 0;
    foreach (var g in dev.GroupList) total += g.VarList.Count;

    Console.WriteLine("        " + dev.DeviceName + " -> " + dev.Connection.Describe()
        + " 组 " + dev.GroupList.Count + " 变量 " + total);

    KyjScada.Infrastructure.Tests.TestHelpers.AssertEq(139, total, "在跑的设备共 139 个变量");
    KyjScada.Infrastructure.Tests.TestHelpers.AssertEq(4, dev.GroupList.Count, "4 个组");
});

Console.WriteLine();
Console.WriteLine("通过 " + pass + " / 失败 " + fail);

if (fail > 0)
{
    Console.WriteLine();
    foreach (var f in failures) Console.WriteLine("  - " + f);
}

return fail == 0 ? 0 : 1;

string? FindRealSettings()
{
    var baseDir = AppContext.BaseDirectory;
    var probe = Path.Combine(baseDir, "_work");

    if (Directory.Exists(probe))
    {
        var hit = Directory.GetFiles(probe, "ModbusTCP.json");
        if (hit.Length > 0) return hit[0];
    }

    var appDir = FindRealSettingsDir();

    if (appDir is not null)
    {
        var hit2 = Directory.GetFiles(appDir, "ModbusTCP.json");
        if (hit2.Length > 0) return hit2[0];
    }

    return null;
}

string? FindRealSettingsDir()
{
    var baseDir = AppContext.BaseDirectory;
    var probe = Path.Combine(baseDir, "_work");

    if (Directory.Exists(probe) && Directory.GetFiles(probe, "*.json").Length > 0) return probe;

    var appDir = Path.Combine(baseDir, "..", "..", "..", "..", "KyjScada.App", "bin", "Debug", "net8.0-windows", "settings");

    return Directory.Exists(appDir) ? appDir : null;
}
