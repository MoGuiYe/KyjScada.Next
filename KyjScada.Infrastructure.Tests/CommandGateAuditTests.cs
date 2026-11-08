using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 写入审计的"结案"测试。
///
/// 闸门在下发之前就把审计记成一行(此时 Outcome 是 null),下发之后再回填结果。
/// 这条链有两个致命断点,而且都不会抛异常、不会报错,只是事后查不到真相:
///  1. **悬空审计** —— 写入器返回失败或抛异常时忘了回填,那条记录永远既不像成功
///     也不像失败。出了事故翻审计,分不清到底下发没有。
///  2. **串案** —— 并发的两次写入互相覆盖结果(早先按变量名找"最近一条未结案"),
///     于是失败那次被标成功,或反过来。"审计说写成功了,设备其实没收到"是最坏的结论。
/// 另外,失败那次占用的限速名额必须撤掉,否则"设备报错 → 立刻重试"会被当成操作太频繁。
/// </summary>
public static class CommandGateAuditTests
{
    public static void RunAll(Action<string, Action> run)
    {
        run("审计:写入成功结案为 ok 且带流水号", () =>
        {
            var gate = MakeGate(out _);
            gate.Writer = new FakeWriter(v => true, null);

            var outcome = gate.Write("SET1", 5.5);

            TestHelpers.AssertTrue(outcome.Success, "写入应成功,实际 " + outcome.Verdict + " / " + outcome.Message);

            var audit = gate.Audit;

            TestHelpers.AssertEq(1, audit.Count, "应有一条审计");
            TestHelpers.AssertStr("ok", audit[0].Outcome ?? "", "成功写入必须结案为 ok");
            TestHelpers.AssertTrue(audit[0].Id > 0, "审计必须带流水号,否则无法回填结果");
        });

        run("审计:设备返回失败结案为 fail,且失败不占用限速名额", () =>
        {
            // 限速故意设大:如果失败那次把名额占住了,紧接着的重试会被拒绝,
            // 现场看到的就是"通讯失败"变成"操作太频繁",完全误导。
            var gate = MakeGate(out _);
            gate.MinInterval = TimeSpan.FromMilliseconds(3000);
            gate.Writer = new FakeWriter(v => false, "设备无响应");

            var first = gate.Write("SET1", 1.0);

            TestHelpers.AssertTrue(!first.Success, "第一次应失败");
            TestHelpers.AssertTrue(first.Message.Contains("设备无响应"), "应透出设备给的原因,实际 " + first.Message);
            TestHelpers.AssertStr("fail", gate.Audit[0].Outcome ?? "", "失败写入也必须结案(不能悬空)");

            gate.Writer = new FakeWriter(v => true, null);

            var second = gate.Write("SET1", 1.0);

            TestHelpers.AssertTrue(second.Success,
                "失败后应能立即重试,实际被拦:" + second.Message);
            TestHelpers.AssertStr("ok", gate.Audit[1].Outcome ?? "", "重试那条应结案为 ok");
        });

        run("审计:写入器抛异常时也结案为 fail,异常照常上抛", () =>
        {
            var gate = MakeGate(out _);
            gate.Writer = new FakeWriter(v => throw new IOException("串口断了"), null);

            var threw = false;

            try
            {
                gate.Write("SET1", 1.0);
            }
            catch (IOException)
            {
                threw = true;
            }

            TestHelpers.AssertTrue(threw, "异常必须上抛给调用方");
            TestHelpers.AssertEq(1, gate.Audit.Count, "异常那次也要留下审计");
            TestHelpers.AssertStr("fail", gate.Audit[0].Outcome ?? "", "抛异常那次必须结案为 fail");
        });

        run("审计:同一测点并发写入,结果按流水号回填不串案", () =>
        {
            // 8 个线程写同一个测点(限速放开),值 1..8,偶数值让设备失败。
            // 早先按变量名回填的实现会把结果标到别的写入头上 —— 这里每条都要对得上自己的值。
            var gate = MakeGate(out _);
            gate.MinInterval = TimeSpan.Zero;
            gate.Writer = new FakeWriter(v => ((int)v) % 2 == 1, "偶数一律失败");

            var values = new double[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var threads = new List<Thread>();

            foreach (var v in values)
            {
                var value = v;
                var th = new Thread(() => gate.Write("SET1", value));
                threads.Add(th);
            }

            foreach (var th in threads) th.Start();
            foreach (var th in threads) th.Join();

            var audit = gate.Audit;

            TestHelpers.AssertEq(values.Length, audit.Count, "每次写入都应留下审计");

            foreach (var a in audit)
            {
                TestHelpers.AssertTrue(a.Outcome != null,
                    "并发写入也不能留下悬空审计: 值 " + a.Value);

                var value = (double)a.Value!;
                var expected = ((int)value) % 2 == 1 ? "ok" : "fail";

                TestHelpers.AssertStr(expected, a.Outcome!,
                    "值 " + value + " 的结果被标到了错误的记录上");
            }

            var ids = new HashSet<long>();

            foreach (var a in audit)
            {
                TestHelpers.AssertTrue(ids.Add(a.Id), "流水号必须唯一,重复 id " + a.Id);
            }
        });
    }

    /// <summary>造一个开好写开关、只有一个可写点的闸门。</summary>
    private static CommandGate MakeGate(out DeviceConfig device)
    {
        device = new DeviceConfig
        {
            DeviceName = "D1",
            GroupList = new List<GroupConfig>
            {
                new()
                {
                    GroupName = "设定值",
                    IsWritable = true,
                    Start = "1.400001",
                    Length = 10,
                    VarList = new List<VariableConfig>
                    {
                        new() { VarName = "SET1", VarType = "Float", Start = "1.40001" }
                    }
                }
            }
        };

        var gate = new CommandGate
        {
            Enabled = true,
            MinInterval = TimeSpan.Zero
        };

        gate.BuildWritables(device, v => true);

        return gate;
    }

    /// <summary>设备写入器假实现:判定由委托给出,便于分别造成功/失败/异常。</summary>
    private sealed class FakeWriter : IDeviceWriter
    {
        private readonly Func<double, bool> _judge;
        private readonly string? _error;

        public FakeWriter(Func<double, bool> judge, string? error)
        {
            _judge = judge;
            _error = error;
        }

        public bool WriteVariable(string varName, double value, out string error)
        {
            var ok = _judge(value);

            error = ok ? "" : (_error ?? "失败");

            return ok;
        }
    }
}