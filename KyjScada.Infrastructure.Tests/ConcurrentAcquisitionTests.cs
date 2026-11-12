using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Infrastructure.Acquisition;
using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 多设备并发采集的**真网络**验证:两台 Modbus RTU Over TCP 从站(不同端口),
/// 由调度器同时采,要求两边都收到真帧并各自采到正确数值。
///
/// 为什么不用假采集器就够了:
///  假采集器只能证明"循环能并行",证不了"两条连接能同时在真实帧上来回跑"。
///  现场出问题的往往就是后者(连接串了、缓冲抢了、CRC 算错)。
/// </summary>
public static class ConcurrentAcquisitionTests
{
    public static void RunAll(Action<string, Action> run)
    {
        run("并发采集(真帧):两台 RTU Over TCP 设备同时采到正确值", () =>
        {
            using var simA = new ModbusSlaveSimulator (rtuOverTcp: true);
            using var simB = new ModbusSlaveSimulator (rtuOverTcp: true);

            SetFloat (simA, 33.5f);
            SetFloat (simB, 44.25f);

            var devA = RtuDevice ("A", simA.Port, "P_A");
            var devB = RtuDevice ("B", simB.Port, "P_B");

            var coord = new AcquisitionCoordinator (d =>
            {
                var plan = AcquisitionFactory.Create (d);

                return plan.Supported ? plan.Service : null;
            })
            {
                Interval = TimeSpan.FromMilliseconds (50)
            };

            try
            {
                coord.BuildAsync (new[] { devA, devB }).GetAwaiter ().GetResult ();
                TestHelpers.AssertEq (2, coord.StartAll (), "两台都应启动");

                // 给两轮以上的时间
                Thread.Sleep (600);

                var pa = coord.Find ("A");
                var pb = coord.Find ("B");

                TestHelpers.AssertTrue (pa is not null && pb is not null, "两条流水线都应存在");

                // 读回值必须在**停之前**取,停了总线不再更新但值还在
                var va = pa!.Bus.GetValue ("P_A")?.AsDouble ();
                var vb = pb!.Bus.GetValue ("P_B")?.AsDouble ();

                Console.WriteLine ("        A 轮次=" + pa.Rounds + " 点数=" + pa.LastCount + " 值=" + va
                    + " | B 轮次=" + pb.Rounds + " 点数=" + pb.LastCount + " 值=" + vb);

                TestHelpers.AssertTrue (pa.Rounds >= 2, "A 应采到多轮,实际 " + pa.Rounds);
                TestHelpers.AssertTrue (pb.Rounds >= 2, "B 应采到多轮,实际 " + pb.Rounds);

                TestHelpers.AssertTrue (va.HasValue && Math.Abs (va.Value - 33.5) < 0.01,
                    "A 应采到 33.5,实际 " + va);

                TestHelpers.AssertTrue (vb.HasValue && Math.Abs (vb.Value - 44.25) < 0.01,
                    "B 应采到 44.25,实际 " + vb);

                // 真帧计数:证明数据是从两个不同的 socket 上来的
                TestHelpers.AssertTrue (simA.RequestCount >= 2, "A 从站应收到多个请求,实际 " + simA.RequestCount);
                TestHelpers.AssertTrue (simB.RequestCount >= 2, "B 从站应收到多个请求,实际 " + simB.RequestCount);
            }
            finally
            {
                coord.StopAll ();
                coord.Dispose ();
            }
        });

        run("并发采集(真帧):一台从站关掉,另一台照常采", () =>
        {
            var simA = new ModbusSlaveSimulator (rtuOverTcp: true);
            var simB = new ModbusSlaveSimulator (rtuOverTcp: true);

            SetFloat (simA, 11.75f);
            SetFloat (simB, 22.5f);

            var devA = RtuDevice ("A2", simA.Port, "Q_A");
            var devB = RtuDevice ("B2", simB.Port, "Q_B");

            var coord = new AcquisitionCoordinator (d =>
            {
                var plan = AcquisitionFactory.Create (d);

                return plan.Supported ? plan.Service : null;
            })
            {
                Interval = TimeSpan.FromMilliseconds (50)
            };

            try
            {
                coord.BuildAsync (new[] { devA, devB }).GetAwaiter ().GetResult ();
                coord.StartAll ();

                Thread.Sleep (300);

                var pa = coord.Find ("A2")!;
                var beforeB = pa.Rounds - 1;

                // A 停掉监听:相当于现场一台设备掉线/断电
                simA.Dispose ();

                Thread.Sleep (600);

                var vb = coord.Find ("B2")!.Bus.GetValue ("Q_B")?.AsDouble ();

                Console.WriteLine ("        A 状态=" + pa.State + " 轮次=" + pa.Rounds
                    + " | B 轮次=" + coord.Find ("B2")!.Rounds + " 值=" + vb);

                TestHelpers.AssertTrue (pa.Rounds > beforeB, "掉线设备应仍在重试(不能静默停采)");
                TestHelpers.AssertTrue (vb.HasValue && Math.Abs (vb.Value - 22.5) < 0.01,
                    "另一台应继续采到 22.5,实际 " + vb);
            }
            finally
            {
                coord.StopAll ();
                coord.Dispose ();
                simB.Dispose ();
            }
        });
    }

    /// <summary>CDAB 字序写一个 Float 到 4xxxx 起始两个寄存器(和现场配置一致)。</summary>
    private static void SetFloat (ModbusSlaveSimulator sim, float value)
    {
        var b = BitConverter.GetBytes (value);

        if (BitConverter.IsLittleEndian) Array.Reverse (b);

        // ABCD 是 reg0=(A,B) reg1=(C,D);CDAB 交换字序 -> reg0=(C,D) reg1=(A,B)
        var hi = (ushort)((b[0] << 8) | b[1]);
        var lo = (ushort)((b[2] << 8) | b[3]);

        // 4xxxx 起始 0 偏移:组的 Start 就是首地址,变量偏移为 0
        sim.SetRegister (0, lo);
        sim.SetRegister (1, hi);
    }

    private static DeviceConfig RtuDevice (string name, int port, string varName)
    {
        var dev = new DeviceConfig
        {
            DeviceName = name,
            ProtocolKey = "ModbusRTUOverTCP",
            WordOrder = WordOrder.Cdab,
            Connection = new ConnectionConfig { Kind = ConnectionKind.Tcp, IpAddress = "127.0.0.1", Port = port }
        };

        var group = new GroupConfig
        {
            GroupName = "实时组",
            IsActive = true,
            Start = "1.400001",
            Length = 10
        };

        group.VarList.Add (new VariableConfig
        {
            VarName = varName,
            VarType = "Float",
            VarAddress = "1.400001",
            Start = "1.400001",
            Scale = 1f
        });

        dev.GroupList.Add (group);

        return dev;
    }
}