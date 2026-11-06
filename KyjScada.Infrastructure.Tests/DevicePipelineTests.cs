using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 多设备并发采集的验证。
///
/// 这些用例的意义:老系统"看 A 就不采 B",切设备期间 B 的历史是断的。
/// 所以这里必须证明 —— 两台设备的采集是**同时推进**的,
/// 并且一台设备出故障不会把另一台带停。
/// </summary>
public static class DevicePipelineTests
{
    public static void RunAll(Action<string, Action> run)
    {
        run("并发采集:两台设备同时推进,互不等待", () =>
        {
            var a = new FakeService("devA", 0);
            var b = new FakeService("devB", 0);

            var pa = new DevicePipeline(Dev("devA"), new ValueBus(), a) { Interval = TimeSpan.FromMilliseconds(30) };
            var pb = new DevicePipeline(Dev("devB"), new ValueBus(), b) { Interval = TimeSpan.FromMilliseconds(30) };

            pa.Start();
            pb.Start();

            Thread.Sleep(400);

            // 状态要在停之前读:停止后流水线统一标"已停止",看不到运行期状态
            var stateA = pa.State;
            var stateB = pb.State;

            pa.StopAndWait(TimeSpan.FromSeconds(2));
            pb.StopAndWait(TimeSpan.FromSeconds(2));

            // 串行实现时,一轮 A + 一轮 B 共用同一个循环,单台最多也就跑一半的轮次;
            // 这里两台都独立跑满,轮次应该接近。
            TestHelpers.AssertTrue(pa.Rounds >= 3, "devA 应采到多轮,实际 " + pa.Rounds);
            TestHelpers.AssertTrue(pb.Rounds >= 3, "devB 应采到多轮,实际 " + pb.Rounds);

            var ratio = (double)Math.Min(pa.Rounds, pb.Rounds) / Math.Max(pa.Rounds, pb.Rounds);
            TestHelpers.AssertTrue(ratio > 0.5, "两台轮次应接近,实际 A=" + pa.Rounds + " B=" + pb.Rounds);

            TestHelpers.AssertEq(a.Rounds, pa.Rounds, "devA 服务被调用轮次应与流水线一致");
            TestHelpers.AssertEq(b.Rounds, pb.Rounds, "devB 服务被调用轮次应与流水线一致");
            Console.WriteLine("        A 轮次=" + pa.Rounds + " B 轮次=" + pb.Rounds + " 状态=" + stateA + "/" + stateB);
        });

        run("并发采集:一台抛异常不影响另一台", () =>
        {
            var bad = new FakeService("bad") { ThrowOnCollect = true };
            var good = new FakeService("good", 0);

            var pb = new DevicePipeline(Dev("bad"), new ValueBus(), bad) { Interval = TimeSpan.FromMilliseconds(30) };
            var pg = new DevicePipeline(Dev("good"), new ValueBus(), good) { Interval = TimeSpan.FromMilliseconds(30) };

            pb.Start();
            pg.Start();

            Thread.Sleep(350);

            var badState = pb.State;

            pb.StopAndWait(TimeSpan.FromSeconds(2));
            pg.StopAndWait(TimeSpan.FromSeconds(2));

            TestHelpers.AssertStr("异常", badState, "故障设备运行中应标为异常");
            TestHelpers.AssertTrue(bad.Rounds >= 2, "故障设备应持续重试,实际 " + bad.Rounds);
            TestHelpers.AssertTrue(pg.Rounds >= 3, "健康设备应照常采集,实际 " + pg.Rounds);
            TestHelpers.AssertTrue(pb.LastError is { Length: > 0 }, "应记录错误原因");
        });

        run("并发采集:停止后不再采集", () =>
        {
            var svc = new FakeService("stopper", 0);
            var p = new DevicePipeline(Dev("stopper"), new ValueBus(), svc) { Interval = TimeSpan.FromMilliseconds(20) };

            p.Start();
            Thread.Sleep(150);

            TestHelpers.AssertTrue(p.StopAndWait(TimeSpan.FromSeconds(2)), "应在超时内停止");

            var after = svc.Rounds;
            Thread.Sleep(150);

            TestHelpers.AssertEq(after, svc.Rounds, "停止后不应再有采集调用");
            TestHelpers.AssertStr("已停止", p.State, "状态应为已停止");
            TestHelpers.AssertTrue(!p.IsRunning, "不应仍在运行");
        });

        run("并发采集:采集异常时报警与归档跳过(不拿旧值误判)", () =>
        {
            var svc = new FakeService("quiet", 0) { PublishNothing = true };
            var alarm = new AlarmService();
            var p = new DevicePipeline(Dev("quiet"), new ValueBus(), svc, alarm) { Interval = TimeSpan.FromMilliseconds(20) };

            p.Start();
            Thread.Sleep(120);
            p.StopAndWait(TimeSpan.FromSeconds(2));

            // 本轮 0 个点 -> 不跑报警状态机
            TestHelpers.AssertEq(0, p.LastCount, "本轮点数应为 0");
            TestHelpers.AssertEq(0, alarm.GetActiveAlarms().Count, "无数据不应产生报警");
        });

        run("协调器:重复建同一设备返回原实例,新设备追加", () =>
        {
            var coord = new AcquisitionCoordinator(d => new FakeService(d.DeviceName))
            {
                Interval = TimeSpan.FromMilliseconds(20)
            };

            var added1 = coord.Build(new[] { Dev("d1"), Dev("d2") });
            var p1 = coord.Find("d1");

            TestHelpers.AssertEq(2, added1.Count, "首次应建 2 条");
            TestHelpers.AssertTrue(p1 != null, "应能按名字找到");

            // 再建一次(配置刷新):已存在的不能重建,否则会把在线连接掐掉
            var added2 = coord.Build(new[] { Dev("d1"), Dev("d2"), Dev("d3") });

            TestHelpers.AssertEq(1, added2.Count, "只应新增 d3");
            TestHelpers.AssertTrue(ReferenceEquals(p1, coord.Find("d1")), "d1 应保持原实例");
            TestHelpers.AssertEq(3, coord.Pipelines.Count, "台账应有 3 条");

            // 移除 d2
            coord.Build(new[] { Dev("d1"), Dev("d3") });

            TestHelpers.AssertTrue(coord.Find("d2") is null, "d2 应被回收");
            TestHelpers.AssertEq(2, coord.Pipelines.Count, "台账应剩 2 条");

            coord.Dispose();
        });

        run("协调器:不可采设备不启动但保留原因", () =>
        {
            var coord = new AcquisitionCoordinator(d => null) { Interval = TimeSpan.FromMilliseconds(20) };

            coord.Build(new[] { Dev("x") });
            var started = coord.StartAll();
            var p = coord.Find("x");

            TestHelpers.AssertEq(0, started, "不可采设备不应启动");
            TestHelpers.AssertStr("不可采", p!.State, "状态应为不可采");
            TestHelpers.AssertTrue(p.UncollectableReason is { Length: > 0 }, "应给出原因");

            coord.Dispose();
        });

        run("协调器:启动计数与台账描述", () =>
        {
            var coord = new AcquisitionCoordinator(d => new FakeService(d.DeviceName))
            {
                Interval = TimeSpan.FromMilliseconds(25)
            };

            coord.Build(new[] { Dev("m1"), Dev("m2") });

            TestHelpers.AssertEq(2, coord.StartAll(), "应启动 2 条");
            Thread.Sleep(120);

            var desc = coord.Describe();

            TestHelpers.AssertTrue(desc.Contains("m1"), "描述应含 m1");
            TestHelpers.AssertTrue(desc.Contains("m2"), "描述应含 m2");

            coord.StopAll();
            coord.Dispose();
        });
    }

    private static DeviceConfig Dev(string name) => new()
    {
        DeviceName = name,
        ProtocolKey = "ModbusTCP",
        GroupList = new List<GroupConfig>()
    };

    /// <summary>假的采集器:不碰网络,只累加轮次。</summary>
    private sealed class FakeService : IAcquisitionService
    {
        private readonly string _name;
        private readonly int _points;

        public FakeService(string name, int points = 3)
        {
            _name = name;
            _points = points;
        }

        public bool ThrowOnCollect { get; init; }

        public bool PublishNothing { get; init; }

        public int Rounds;

        public string DeviceName => _name;

        public bool IsConnected => true;

        public string? LastError { get; private set; }

        public int CollectOnce(IValueBus bus, DateTime sampleTime)
        {
            Interlocked.Increment(ref Rounds);

            if (ThrowOnCollect) throw new IOException("模拟断线: " + _name);

            if (PublishNothing) return 0;

            var dict = new Dictionary<string, TagValue>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < _points; i++)
            {
                dict[_name + "_P" + i] = new TagValue(_name + "_P" + i, i * 1.0, TagQuality.Good, sampleTime);
            }

            bus.Publish(dict);

            return _points;
        }

        public void Disconnect()
        {
        }

        public void Dispose()
        {
        }
    }
}
