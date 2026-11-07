using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Infrastructure.Alarm;
using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 报警服务接线测试:配置编译、快照处理、状态查询、落库读写。
/// 不依赖真实设备 —— 用合成快照喂状态机。
/// </summary>
public static class AlarmServiceTests
{
    public static void RunAll(Action<string, Action> run)
    {
        RunBuild(run);
        RunProcess(run);
        RunActive(run);
        RunRepository(run);
        RunRealConfig(run);
    }

    // 造一个只含单变量的设备配置
    private static DeviceConfig MakeDevice(
        string varName,
        bool highEnable, float high, bool lowEnable, float low, float deadband = 0)
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
            HighAlarmEnable = highEnable,
            HighAlarmValue = high,
            HighAlarmNote = "高限测试",
            LowAlarmEnable = lowEnable,
            LowAlarmValue = low,
            LowAlarmNote = "低限测试",
            AlarmDeadband = deadband
        });

        device.GroupList.Add(group);
        return device;
    }

    // 造一个只含指定变量值的快照
    private static ValueSnapshot MakeSnapshot(string varName, double? value, DateTime at)
    {
        var dict = new Dictionary<string, TagValue>(StringComparer.OrdinalIgnoreCase);

        if (value is not null)
        {
            dict[varName] = new TagValue(
                varName,
                value.Value,
                TagQuality.Good,
                at);
        }

        return new ValueSnapshot(1, at, true, dict);
    }

    private static void RunBuild(Action<string, Action> run)
    {
        run("报警规则编译:同时启用高低限应产生 2 台状态机", () =>
        {
            var svc = new AlarmService();
            svc.BuildFrom(MakeDevice("V1", true, 100, true, 10));

            TestHelpers.AssertEq(1, svc.WatchedVarCount, "应监视 1 个变量");
            TestHelpers.AssertEq(2, svc.MachineCount, "应有 2 台状态机(高+低)");
        });

        run("报警规则编译:只启用高限应产生 1 台状态机", () =>
        {
            var svc = new AlarmService();
            svc.BuildFrom(MakeDevice("V1", true, 100, false, 0));

            TestHelpers.AssertEq(1, svc.MachineCount, "应只有 1 台状态机");
        });

        run("报警规则编译:未启用报警的变量不占状态机", () =>
        {
            var svc = new AlarmService();
            svc.BuildFrom(MakeDevice("V1", false, 0, false, 0));

            TestHelpers.AssertEq(0, svc.MachineCount, "不应有状态机");
            TestHelpers.AssertEq(0, svc.WatchedVarCount, "不应有监视变量");
        });

        // 采集线程在 ProcessSnapshot/GetActiveAlarms 里枚举状态机槽位,
        // 界面线程在改配置时会 BuildFrom 重建。老实现只锁了"清空"那两行,
        // 填充在锁外 —— 枚举方会撞 Collection was modified(界面线程上等于直接崩)。
        run("报警规则编译:重建与采集并发不抛集合已修改", () =>
        {
            var svc = new AlarmService();
            var dev = MakeDevice("V1", true, 100, true, 10);

            svc.BuildFrom(dev);

            var snap = new ValueSnapshot(1, DateTime.Now, true,
                new Dictionary<string, TagValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["V1"] = new TagValue("V1", 200.0, TagQuality.Good, DateTime.Now)
                });

            var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
            var stop = false;

            var worker = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    try
                    {
                        svc.ProcessSnapshot(snap);
                        svc.GetActiveAlarms();
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex.GetType().Name + ": " + ex.Message);
                    }
                }
            });

            worker.Start();

            for (int i = 0; i < 200; i++) svc.BuildFrom(dev);

            Volatile.Write(ref stop, true);
            worker.Join();

            TestHelpers.AssertEq(0, errors.Count, "并发重建不应抛异常,实际第一条: "
                + (errors.Count > 0 ? errors.First() : ""));
            TestHelpers.AssertEq(2, svc.MachineCount, "重建后仍应有 2 台状态机");
        });

        run("报警规则编译:NaN/Infinity 阈值被忽略", () =>
        {
            var svc = new AlarmService();
            svc.BuildFrom(MakeDevice("V1", true, float.NaN, true, float.PositiveInfinity));

            TestHelpers.AssertEq(0, svc.MachineCount, "非有限阈值不应建状态机");
        });
    }

    private static void RunProcess(Action<string, Action> run)
    {
        run("报警处理:超上限触发一次事件", () =>
        {
            var svc = new AlarmService();
            svc.BuildFrom(MakeDevice("V1", true, 100, false, 0));

            var t = new DateTime(2026, 11, 4, 10, 0, 0);

            // 第一次:150 > 100,触发
            var ev1 = svc.ProcessSnapshot(MakeSnapshot("V1", 150, t));
            TestHelpers.AssertEq(1, ev1.Count, "应有 1 个事件");
            TestHelpers.AssertTrue(ev1[0].IsTrigger, "应为触发事件");
            TestHelpers.AssertTrue(ev1[0].Direction == AlarmDirection.High, "方向应为上限");

            // 第二次:仍在报警中,无新事件
            var ev2 = svc.ProcessSnapshot(MakeSnapshot("V1", 160, t.AddSeconds(1)));
            TestHelpers.AssertEq(0, ev2.Count, "持续报警不应产生新事件");
        });

        run("报警处理:降回阈值以下产生消除事件", () =>
        {
            var svc = new AlarmService();
            svc.BuildFrom(MakeDevice("V1", true, 100, false, 0));

            var t = new DateTime(2026, 11, 4, 10, 0, 0);

            svc.ProcessSnapshot(MakeSnapshot("V1", 150, t));

            var ev = svc.ProcessSnapshot(MakeSnapshot("V1", 50, t.AddSeconds(1)));
            TestHelpers.AssertEq(1, ev.Count, "应有 1 个消除事件");
            TestHelpers.AssertTrue(!ev[0].IsTrigger, "应为消除事件");
        });

        run("报警处理:死区内不抖动(100 次穿越只产生 1 次触发)", () =>
        {
            var svc = new AlarmService();
            svc.BuildFrom(MakeDevice("V1", true, 100, false, 0, deadband: 2));

            var t = new DateTime(2026, 11, 4, 10, 0, 0);
            var triggerCount = 0;

            for (int i = 0; i < 100; i++)
            {
                // 在 99~101 之间反复穿越阈值 100
                var v = i % 2 == 0 ? 99.0 : 101.0;
                var ev = svc.ProcessSnapshot(MakeSnapshot("V1", v, t.AddSeconds(i)));
                triggerCount += ev.Count(e => e.IsTrigger);
            }

            TestHelpers.AssertEq(1, triggerCount, "死区内应只触发 1 次,实际 " + triggerCount);
        });

        run("报警处理:null 值不改变状态(断线≠消除)", () =>
        {
            var svc = new AlarmService();
            svc.BuildFrom(MakeDevice("V1", true, 100, false, 0));

            var t = new DateTime(2026, 11, 4, 10, 0, 0);

            svc.ProcessSnapshot(MakeSnapshot("V1", 150, t));

            // 断线:值为 null
            var ev = svc.ProcessSnapshot(MakeSnapshot("V1", null, t.AddSeconds(1)));
            TestHelpers.AssertEq(0, ev.Count, "null 值不应产生事件");

            // 仍是报警状态
            TestHelpers.AssertEq(1, svc.GetActiveAlarms().Count, "断线期间报警应保持");
        });

        run("报警处理:低限报警触发与消除", () =>
        {
            var svc = new AlarmService();
            svc.BuildFrom(MakeDevice("V1", false, 0, true, 10));

            var t = new DateTime(2026, 11, 4, 10, 0, 0);

            var ev1 = svc.ProcessSnapshot(MakeSnapshot("V1", 5, t));
            TestHelpers.AssertEq(1, ev1.Count, "应有 1 个事件");
            TestHelpers.AssertTrue(ev1[0].IsTrigger, "应为触发");
            TestHelpers.AssertTrue(ev1[0].Direction == AlarmDirection.Low, "方向应为下限");

            var ev2 = svc.ProcessSnapshot(MakeSnapshot("V1", 20, t.AddSeconds(1)));
            TestHelpers.AssertEq(1, ev2.Count, "应有 1 个消除");
            TestHelpers.AssertTrue(!ev2[0].IsTrigger, "应为消除");
        });

        run("报警处理:同时配高低限,两台状态机互不干扰", () =>
        {
            var svc = new AlarmService();
            svc.BuildFrom(MakeDevice("V1", true, 100, true, 10));

            var t = new DateTime(2026, 11, 4, 10, 0, 0);

            // 高限触发
            var ev = svc.ProcessSnapshot(MakeSnapshot("V1", 150, t));
            TestHelpers.AssertEq(1, ev.Count, "应只有高限触发");
            TestHelpers.AssertTrue(ev[0].Direction == AlarmDirection.High, "应为高限");
        });
    }

    private static void RunActive(Action<string, Action> run)
    {
        run("活动报警:触发后可查到,带阈值和当前值", () =>
        {
            var svc = new AlarmService();
            svc.BuildFrom(MakeDevice("V1", true, 100, false, 0));

            var t = new DateTime(2026, 11, 4, 10, 0, 0);
            TestHelpers.AssertEq(0, svc.GetActiveAlarms().Count, "初始不应有活动报警");

            svc.ProcessSnapshot(MakeSnapshot("V1", 150, t));

            var active = svc.GetActiveAlarms();
            TestHelpers.AssertEq(1, active.Count, "应有 1 个活动报警");
            TestHelpers.AssertStr("V1", active[0].VarName, "变量名错误");
            TestHelpers.AssertTrue(Math.Abs(active[0].Threshold - 100.0) < 1e-9, "阈值错误");
            TestHelpers.AssertTrue(Math.Abs(active[0].Value - 150.0) < 1e-9, "当前值错误");
            TestHelpers.AssertTrue(active[0].TriggeredAt == t, "触发时刻错误");
        });

        run("活动报警:消除后从列表移除", () =>
        {
            var svc = new AlarmService();
            svc.BuildFrom(MakeDevice("V1", true, 100, false, 0));

            var t = new DateTime(2026, 11, 4, 10, 0, 0);
            svc.ProcessSnapshot(MakeSnapshot("V1", 150, t));
            TestHelpers.AssertEq(1, svc.GetActiveAlarms().Count, "应有报警");

            svc.ProcessSnapshot(MakeSnapshot("V1", 50, t.AddSeconds(1)));
            TestHelpers.AssertEq(0, svc.GetActiveAlarms().Count, "消除后不应有报警");
        });
    }

    private static void RunRepository(Action<string, Action> run)
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, "_work", "alarm_test.db");

        run("报警库建表幂等", () =>
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
            var repo = new AlarmRepository(dbPath);
            repo.EnsureSchema();
            repo.EnsureSchema();
        });

        run("报警事件落库与查询", () =>
        {
            var repo = new AlarmRepository(dbPath);
            var t = new DateTime(2026, 11, 4, 10, 0, 0);

            var events = new List<AlarmEvent>
            {
                new("V1", AlarmDirection.High, AlarmLevel.Normal, AlarmLevel.Active, 150, t),
                new("V1", AlarmDirection.High, AlarmLevel.Active, AlarmLevel.Normal, 50, t.AddSeconds(10))
            };

            var written = repo.WriteEvents(events);
            TestHelpers.AssertEq(2, written, "应写入 2 条");

            var found = repo.Query("V1");
            TestHelpers.AssertEq(2, found.Count, "应查到 2 条");

            // 倒序:最新在前
            TestHelpers.AssertTrue(found[0].IsTrigger == false, "第一条应是消除事件");
            TestHelpers.AssertTrue(found[1].IsTrigger == true, "第二条应是触发事件");
        });

        run("报警事件空批不写", () =>
        {
            var repo = new AlarmRepository(dbPath);
            TestHelpers.AssertEq(0, repo.WriteEvents(new List<AlarmEvent>()), "空批应返回 0");
        });

        run("报警事件按时间删除", () =>
        {
            var repo = new AlarmRepository(dbPath);
            var old = new DateTime(2020, 1, 1, 0, 0, 0);

            repo.WriteEvents(new List<AlarmEvent>
            {
                new("OldVar", AlarmDirection.High, AlarmLevel.Normal, AlarmLevel.Active, 1, old)
            });

            var deleted = repo.DeleteBefore(new DateTime(2021, 1, 1));
            TestHelpers.AssertTrue(deleted >= 1, "应删除至少 1 条");
        });
    }

    private static void RunRealConfig(Action<string, Action> run)
    {
        run("现场配置:4 个变量启用了报警", () =>
        {
            var path = FindRealSettings();
            if (path is null) return;

            var projects = KyjScada.Domain.Config.SettingsJsonCodec.LoadFile(path);
            var project = KyjScada.Domain.Config.SettingsJsonCodec.ActiveProject(projects);
            var dev = project.AllDevices().First();

            var svc = new AlarmService();
            svc.BuildFrom(dev);

            Console.WriteLine("        监视变量 " + svc.WatchedVarCount + " 个,状态机 " + svc.MachineCount + " 台");

            TestHelpers.AssertEq(4, svc.WatchedVarCount, "现场应有 4 个报警变量");
            TestHelpers.AssertTrue(svc.MachineCount >= 4, "状态机数应 >= 4");
        });
    }



    /// <summary>现场校验:App 运行时写入的报警库能读出事件。</summary>
    public static void VerifyLiveAlarmDb()
    {
        var db = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "KyjScada.App", "bin", "Debug", "net8.0-windows", "data", "alarms.db");
        db = Path.GetFullPath(db);

        if (!File.Exists(db)) { Console.WriteLine("报警库不存在: " + db); return; }

        var repo = new AlarmRepository(db);
        var rows = repo.Query(null, null, null, 50);

        Console.WriteLine("        报警库事件总数(最近 50): " + rows.Count);
        foreach (var r in rows)
        {
            Console.WriteLine("          " + r.VarName + " dir=" + r.Direction + " trigger=" + r.IsTrigger + " val=" + r.Value.ToString("F2") + " at=" + r.At.ToString("HH:mm:ss"));
        }

        // 关键诊断:带时间过滤的查询(趋势图事件竖线走这条路径)
        var now = DateTime.Now;
        var from = now.AddHours(-1);

        var filtered = repo.Query(null, from, now, 500);

        Console.WriteLine("        最近 1 小时事件数: " + filtered.Count + "  (区间 "
            + from.ToString("HH:mm:ss") + " ~ " + now.ToString("HH:mm:ss") + ")");

        var triggers = filtered.Count(r => r.IsTrigger);
        Console.WriteLine("        其中触发事件: " + triggers);

        if (filtered.Count > 0)
        {
            Console.WriteLine("        最早 " + filtered.Min(r => r.At).ToString("HH:mm:ss")
                + "  最晚 " + filtered.Max(r => r.At).ToString("HH:mm:ss"));
        }
    }
    /// <summary>找现场配置文件:先看测试目录,再看 App 输出目录。</summary>
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
