using KyjScada.Contracts;
using KyjScada.Host;
using KyjScada.Runtime;
using KyjScada.Verify;

namespace KyjScada.Verify;

public static class Program
{
    private static int _pass;
    private static int _fail;

    public static async Task Main ()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine ("==== 模块化架构实证 ====\n");

        await Test1_IndependentRun ();
        await Test2_FaultIsolation ();
        await Test3_DependencyCheck ();
        await Test4_WriteConflict ();
        await Test5_NullSemantics ();
        await Test6_SnapshotConsistency ();

        Console.WriteLine ($"\n==== 结果: 通过 {_pass} / 失败 {_fail} / 合计 {_pass + _fail} ====");

        if
        (_fail > 0) Environment.ExitCode = 1;
    }

    private static void Check (string label, bool ok, string detail = "")
    {
        if
        (ok)
        {
            _pass++;
            Console.WriteLine ($"PASS  {label}");
        }
        else
        {
            _fail++;
            Console.WriteLine ($"FAIL  {label}  <- {detail}");
        }
    }

    /// <summary>
    /// 测试1:模块能单独加载运行。
    /// 只给 onlyIds=[collector],其他模块不加载。
    /// </summary>
    private static async Task Test1_IndependentRun ()
    {
        Console.WriteLine ("-- 测试1: 模块独立运行 --");

        var log = new HostLogger (sink: e => Console.WriteLine ("   " + e));
        var host = new ModuleHost (log, new DictConfigSource ());

        var bus = new ValueBus ();
        host.Provide<IValueReader> (bus);
        host.Provide<IValueWriter> (bus);
        host.Provide<IValueBus> (bus);

        var modules = new IModule[] { new CollectorModule (), new TrendModule () };

        // 只加载 collector,不加载 trend
        var report = host.Load (modules, new[] { "collector" });

        Check ("只加载 collector 成功", report.Loaded.Count == 1 && report.Loaded[0] == "collector", string.Join (",", report.Loaded));
        Check ("trend 未被加载", !report.Loaded.Contains ("trend.realtime"), string.Join (",", report.Loaded));

        await host.StartAllAsync (CancellationToken.None);

        var snap = bus.GetSnapshot ();
        Check ("独立运行后总线有值", snap.Values.Count == 3, "count=" + snap.Values.Count);

        await host.StopAllAsync (CancellationToken.None);
        Console.WriteLine ();
    }

    /// <summary>
    /// 测试2:一个模块 Init 崩了,其他模块照常加载。
    /// 这是「模块化不能拖垮全系统」的核心验证。
    /// </summary>
    private static async Task Test2_FaultIsolation ()
    {
        Console.WriteLine ("-- 测试2: 故障隔离 --");

        var log = new HostLogger (sink: e => Console.WriteLine ("   " + e));
        var host = new ModuleHost (log, new DictConfigSource ());

        var bus = new ValueBus ();
        host.Provide<IValueReader> (bus);
        host.Provide<IValueWriter> (bus);

        var modules = new IModule[] { new BrokenInitModule (), new CollectorModule () };

        var report = host.Load (modules);

        Check ("崩溃模块被记录", report.Failed.Count == 1, "failed=" + report.Failed.Count);
        Check ("崩溃模块 Id 正确", report.Failed.Count == 1 && report.Failed[0].Id == "broken.init", "");
        Check ("其他模块照常加载", report.Loaded.Contains ("collector"), string.Join (",", report.Loaded));

        await host.StartAllAsync (CancellationToken.None);

        var snap = bus.GetSnapshot ();
        Check ("崩溃后总线仍有值", snap.Values.Count == 3, "count=" + snap.Values.Count);

        await host.StopAllAsync (CancellationToken.None);
        Console.WriteLine ();
    }

    /// <summary>
    /// 测试3:依赖缺失时拒绝加载,而不是运行时崩。
    /// </summary>
    private static async Task Test3_DependencyCheck ()
    {
        Console.WriteLine ("-- 测试3: 依赖校验 --");

        var log = new HostLogger (sink: e => Console.WriteLine ("   " + e));
        var host = new ModuleHost (log, new DictConfigSource ());

        var bus = new ValueBus ();
        host.Provide<IValueReader> (bus);
        host.Provide<IValueWriter> (bus);

        // 故意不提供 IAlarmSink
        var modules = new IModule[] { new MissingDepModule (), new CollectorModule () };
        var report = host.Load (modules);

        Check ("依赖缺失被拒绝", report.Rejected.Count == 1, "rejected=" + report.Rejected.Count);
        Check ("拒绝原因含服务名", report.Rejected.Count == 1 && report.Rejected[0].Reason.Contains ("IAlarmSink"), report.Rejected.Count == 1 ? report.Rejected[0].Reason : "");
        Check ("其他模块不受影响", report.Loaded.Contains ("collector"), string.Join (",", report.Loaded));

        Console.WriteLine ();
    }

    /// <summary>
    /// 测试4:两个模块想写同一测点,必须被拦。
    /// 这是模块化最危险的场景。
    /// </summary>
    private static async Task Test4_WriteConflict ()
    {
        Console.WriteLine ("-- 测试4: 写冲突拦截 --");

        var gate = new CommandGate ();
        gate.MinInterval = TimeSpan.Zero;

        var r1 = gate.Claim ("KYJ1_Start", "hmi");
        Check ("hmi 申请写权成功", r1 == null, r1 ?? "");

        var r2 = gate.Claim ("KYJ1_Start", "mcp");
        Check ("mcp 申请同一测点被拒", r2 != null, "应被拒但通过了");
        Check ("拒绝原因含占用者", r2 != null && r2.Contains ("hmi"), r2 ?? "");

        var w1 = gate.TryEnter ("KYJ1_Start", "mcp", true);
        Check ("mcp 强行写被拦", w1 != null, "应被拦但通过了");

        var w2 = gate.TryEnter ("KYJ1_Start", "hmi", true);
        Check ("hmi 写通过", w2 == null, w2 ?? "");

        gate.Release ("KYJ1_Start", "hmi");
        var r3 = gate.Claim ("KYJ1_Start", "mcp");
        Check ("释放后 mcp 可申请", r3 == null, r3 ?? "");

        Check ("审计记录了写入", gate.Audit.Count >= 1, "audit=" + gate.Audit.Count);

        // 速率限制
        var gate2 = new CommandGate ();
        gate2.MinInterval = TimeSpan.FromMilliseconds (500);
        gate2.Claim ("T1", "m");
        var a = gate2.TryEnter ("T1", "m", 1);
        var b = gate2.TryEnter ("T1", "m", 2);
        Check ("首次写通过", a == null, a ?? "");
        Check ("500ms 内二次写被拦", b != null, "应被拦但通过了");

        Console.WriteLine ();
    }

    /// <summary>
    /// 测试5:NULL 不能被当成 0/false。
    /// 旧系统 LQB1_Start 96% 是 NULL 的真实教训。
    /// </summary>
    private static async Task Test5_NullSemantics ()
    {
        Console.WriteLine ("-- 测试5: NULL 语义 --");

        var bus = new ValueBus ();
        var dict = new Dictionary<string, TagValue> ()
        {
            ["N"] = new TagValue ("N", null, TagQuality.Bad, DateTime.Now),
            ["F"] = new TagValue ("F", false, TagQuality.Good, DateTime.Now),
            ["Z"] = new TagValue ("Z", 0.0, TagQuality.Good, DateTime.Now)
        };

        bus.Publish (dict);
        var snap = bus.GetSnapshot ();

        Check ("NULL.AsBool 返回 null", snap["N"]!.AsBool () == null, snap["N"]!.AsBool ()?.ToString () ?? "null");
        Check ("false.AsBool 返回 false", snap["F"]!.AsBool () == false, "");
        Check ("0.AsBool 返回 false", snap["Z"]!.AsBool () == false, "");
        Check ("NULL.AsDouble 返回 null", snap["N"]!.AsDouble () == null, "");
        Check ("NULL 与 false 可区分", snap["N"]!.AsBool () != snap["F"]!.AsBool (), "无法区分");

        Console.WriteLine ();
    }

    /// <summary>
    /// 测试6:快照一致性。同一 Version 内所有值来自同一次采集。
    /// </summary>
    private static async Task Test6_SnapshotConsistency ()
    {
        Console.WriteLine ("-- 测试6: 快照一致性 --");

        var bus = new ValueBus ();

        bus.Publish (new Dictionary<string, TagValue>
        {
            ["A"] = new TagValue ("A", 1.0, TagQuality.Good, DateTime.Now)
        });

        var snap1 = bus.GetSnapshot ();

        bus.Publish (new Dictionary<string, TagValue>
        {
            ["A"] = new TagValue ("A", 2.0, TagQuality.Good, DateTime.Now)
        });

        var snap2 = bus.GetSnapshot ();

        Check ("版本自增", snap2.Version > snap1.Version, $"{snap1.Version} -> {snap2.Version}");
        Check ("旧快照不变(1.0)", snap1["A"]!.AsDouble () == 1.0, snap1["A"]!.AsDouble ()?.ToString () ?? "null");
        Check ("新快照更新(2.0)", snap2["A"]!.AsDouble () == 2.0, snap2["A"]!.AsDouble ()?.ToString () ?? "null");
        Check ("同一快照内版本号一致", snap1.Version == 1, "");

        Console.WriteLine ();
    }
}
