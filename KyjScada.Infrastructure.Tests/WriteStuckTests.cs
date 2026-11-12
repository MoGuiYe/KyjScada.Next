using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 写入卡死的自愈用例。
///
/// 背景(本轮回归):闸门给"等上一帧写完"加了 10 秒超时,但那**只保护了排队者** ——
/// 如果 Writer.WriteVariable 真的卡死(串口句柄失效、设备不回帧),那一帧永不返回,
/// _inFlight 被永久占用,于是:
///   · 之后每一次写都要等满 10 秒才失败(现场:"程序还活着,但写什么都等十秒然后失败");
///   · WaitForWritesAsync 每次都超时,释放流水线永远等不到"写完了";
///   · Writer 还指着那个卡死的服务,没有任何自愈路径。
/// 现在超时要顺带宣告故障:摘 Writer、换掉那把被占死的信号量、通知宿主重建连接。
/// </summary>
public static class WriteStuckTests
{
    public static void RunAll (Action<string, Action> run)
    {
        run ("闸门:写入卡死后必须自愈(后续写立刻失败 + 只报一次 + 重建后恢复)", () =>
        {
            var gate = new CommandGate
            {
                Enabled = true,
                MinInterval = TimeSpan.Zero,
                WriteSendTimeout = TimeSpan.FromMilliseconds (300)
            };

            gate.BuildWritables (Dev ("V1", "V2", "V3", "V4"), _ => true);

            var hanging = new HangingWriter ();

            gate.Writer = hanging;

            var stuckReported = new ManualResetEventSlim (false);
            var stuckCount = 0;

            gate.WriteStuckDetected = _ =>
            {
                Interlocked.Increment (ref stuckCount);
                stuckReported.Set ();
            };

            // 第一帧:进入设备写入后**永不返回**(放后台线程,别把测试线程也卡住)
            var first = Task.Run (() => gate.Write ("V1", 1));

            TestHelpers.AssertTrue (hanging.Entered.Wait (TimeSpan.FromSeconds (5)),
                "第一帧应已进入设备写入");

            // 两个排队者同时等这一帧 —— 两个都会超时,但故障只该报一次
            var second = Task.Run (() => gate.Write ("V2", 2));
            var third = Task.Run (() => gate.Write ("V3", 3));

            TestHelpers.AssertTrue (second.Wait (TimeSpan.FromSeconds (5)), "第二帧应已结束(超时失败)");
            TestHelpers.AssertTrue (third.Wait (TimeSpan.FromSeconds (5)), "第三帧应已结束(超时失败)");

            TestHelpers.AssertTrue (!second.Result.Success, "上一帧卡死时本次写必须失败");
            TestHelpers.AssertTrue (stuckReported.Wait (TimeSpan.FromSeconds (2)), "必须报出写入卡死");
            TestHelpers.AssertEq (1, Volatile.Read (ref stuckCount), "卡死只该报一次(否则每次写都触发一次重建)");

            TestHelpers.AssertTrue (gate.WriteStuck, "闸门应标记故障");
            TestHelpers.AssertTrue (gate.Writer is null, "卡死后必须摘掉 Writer,不再往那条连接下发");
            TestHelpers.AssertTrue (gate.WriteStuckReason is { Length: > 0 }, "应记下卡死原因");

            // 故障未排除前,后续写必须**立刻**失败 —— 不能又陪跑一个超时
            var sw = System.Diagnostics.Stopwatch.StartNew ();
            var fourth = gate.Write ("V4", 4);
            sw.Stop ();

            TestHelpers.AssertTrue (!fourth.Success, "故障未排除前写入必须失败");
            TestHelpers.AssertTrue (sw.ElapsedMilliseconds < 150,
                "卡死后后续写必须立刻失败(不能每次都白等满超时),实际 " + sw.ElapsedMilliseconds + " ms");

            // 释放流水线前的"等在途写结束"也得恢复:否则每次释放都要白等满
            var waitSw = System.Diagnostics.Stopwatch.StartNew ();
            var drained = gate.WaitForWritesAsync (TimeSpan.FromSeconds (2)).GetAwaiter ().GetResult ();
            waitSw.Stop ();

            TestHelpers.AssertTrue (drained, "判定卡死并换掉信号量后,等'在途写结束'不该再永远超时");
            TestHelpers.AssertTrue (waitSw.ElapsedMilliseconds < 500,
                "应立刻成功,实际 " + waitSw.ElapsedMilliseconds + " ms");

            // 宿主已重建:接上新写入器 = 故障清除、写入恢复。
            // **必须走 WriterConfirmed**:普通 Writer setter 只换引用、不清标记 ——
            // 否则建页路径(BuildWritePage 无条件 _gate.Writer = _svc)在重建完成前
            // 把旧服务挂回来,就会顺手抹掉故障标记(本轮 M1)。
            hanging.Release ();
            gate.WriterConfirmed (new OkWriter ());

            TestHelpers.AssertTrue (!gate.WriteStuck, "接上新写入器后故障标记应清除");
            TestHelpers.AssertTrue (gate.Write ("V4", 4).Success, "重建后写入应恢复");

            TestHelpers.AssertTrue (first.Wait (TimeSpan.FromSeconds (5)), "卡住的那一帧最终应能收尾(不阻塞测试退出)");
        });

        run ("闸门:重建未完成前建页把旧服务挂回来,不能抹掉\"写入卡死\"标记", () =>
        {
            // 本轮 M1:BuildWritePage 无条件 _gate.Writer = _svc as IDeviceWriter。
            // 卡死后 _svc 仍是那条卡死的服务,只要在重建完成前建一次页
            // (重读组态/换设备续体),旧 setter 就会把故障标记清掉 —— M3 的自愈被无声抵消。
            var gate = new CommandGate
            {
                Enabled = true,
                MinInterval = TimeSpan.Zero,
                WriteSendTimeout = TimeSpan.FromMilliseconds (300)
            };

            gate.BuildWritables (Dev ("V1", "V2"), _ => true);

            var hanging = new HangingWriter ();

            gate.Writer = hanging;

            var first = Task.Run (() => gate.Write ("V1", 1));

            TestHelpers.AssertTrue (hanging.Entered.Wait (TimeSpan.FromSeconds (5)), "第一帧应已进入设备写入");

            TestHelpers.AssertTrue (!gate.Write ("V2", 2).Success, "上一帧卡死时本次写必须失败");
            TestHelpers.AssertTrue (gate.WriteStuck, "闸门已标记写入卡死");

            // 重建完成之前的建页:把它又挂一次(与 BuildWritePage 的动作相同)
            gate.Writer = hanging;

            TestHelpers.AssertTrue (gate.WriteStuck,
                "重建完成前重新挂上同一个旧服务,不该被当成\"故障已排除\"");

            TestHelpers.AssertTrue (!gate.Write ("V2", 2).Success,
                "故障标记还在,写入必须继续立刻失败(不能又陪跑超时)");

            // 这正是早先漏掉的那一半:宿主的重建成功路径要显式确认新连接
            hanging.Release ();
            gate.WriterConfirmed (new OkWriter ());

            TestHelpers.AssertTrue (!gate.WriteStuck, "重建成功路径确认后故障标记才该清除");
            TestHelpers.AssertTrue (gate.Write ("V2", 2).Success, "重建后写入应恢复");
        });

    }

    private static DeviceConfig Dev (params string[] varNames)
    {
        var dev = new DeviceConfig { DeviceName = "stuck", ProtocolKey = "ModbusTCP" };

        var group = new GroupConfig
        {
            GroupName = "写组",
            IsActive = true,
            Start = "1.400001",
            Length = 10
        };

        foreach (var name in varNames)
        {
            group.VarList.Add (new VariableConfig
            {
                VarName = name,
                VarType = "Float",
                VarAddress = "1.400001",
                Start = "1.400001",
                Scale = 1f
            });
        }

        dev.GroupList.Add (group);

        return dev;
    }

    /// <summary>模拟"卡死在一次同步往返里"的写入器:进去就不出来,直到测试放它走。</summary>
    private sealed class HangingWriter : IDeviceWriter
    {
        private readonly ManualResetEventSlim _release = new (false);

        public ManualResetEventSlim Entered { get; } = new (false);

        public bool WriteVariable (string varName, double value, out string error)
        {
            Entered.Set ();

            _release.Wait (TimeSpan.FromSeconds (10));

            error = "";

            return true;
        }

        public void Release () => _release.Set ();
    }

    private sealed class OkWriter : IDeviceWriter
    {
        public bool WriteVariable (string varName, double value, out string error)
        {
            error = "";

            return true;
        }
    }
}
