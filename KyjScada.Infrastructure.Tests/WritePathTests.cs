using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Infrastructure.Acquisition;
using KyjScada.Infrastructure.Modbus;
using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 写入路径测试:值编码(4 种字节序)+ 写入闸门规则。
///
/// 编码必须与读取解码互逆,否则"设了 5.5,读回来是 0"。
/// 这里用现场实测过的寄存器值做锚点:
/// 模拟器存 5.5 时,CDAB 下寄存器正是 [0x0000, 0x40B0]。
/// </summary>
public static class WritePathTests
{
    public static void RunAll(Action<string, Action> run)
    {

        RunDiag(run);
        RunCodec(run);
        RunGate(run);
    }

    private static VariableConfig Var(string name, string type)
        => new() { VarName = name, VarType = type, Start = "1.400101", Scale = 1f };

    private static void RunDiag(Action<string, Action> run)
    {
        run("诊断:地址解析符合现场三种写法", () =>
        {
            // 现场 Start 有纯偏移(100)、偏移.位号(200.01)、完整地址(1.40001)三种
            var cases = new (string Start, string VarAddr, int ExpectAbs, int ExpectBit)[]
            {
                ("1.40001", "1.40001", 0, 0),     // 完整地址 站号.寄存器
                ("0", "1.40001", 0, 0),           // 纯偏移
                ("100", "1.40101", 100, 0),       // 纯偏移(设定值组)
                ("200.01", "1.40201.01", 200, 1), // 偏移.位号
                ("40001", "1.40001", 0, 0)        // 4xxxx 写法归一到 0
            };

            foreach (var c in cases)
            {
                var v = new VariableConfig { VarName = "X", VarType = "Float", Start = c.Start, VarAddress = c.VarAddr };

                var text = ModbusAcquisitionService.DescribeAddress(v);

                Console.WriteLine("        " + text);

                TestHelpers.AssertTrue(text.Contains("abs=" + c.ExpectAbs + " "),
                    "地址 " + c.Start + " 应解析为 abs=" + c.ExpectAbs + ",实际 " + text);

                TestHelpers.AssertTrue(text.Contains("bit=" + c.ExpectBit),
                    "地址 " + c.Start + " 应解析为 bit=" + c.ExpectBit + ",实际 " + text);
            }
        });
    }

    private static void RunCodec(Action<string, Action> run)
    {
        run("写入编码:Float 5.5 在 CDAB 下应为 [0x0000, 0x40B0](现场实测锚点)", () =>
        {
            var regs = ModbusValueCodec.Encode(5.5, Var("V", "Float"), WordOrder.Cdab);

            TestHelpers.AssertTrue(regs is not null, "应能编码");
            TestHelpers.AssertEq(2, regs!.Length, "Float 占 2 寄存器");
            TestHelpers.AssertEq(0x0000, regs[0], "低字在前");
            TestHelpers.AssertEq(0x40B0, regs[1], "高字在后");
        });

        run("写入编码:Float 5.5 在 ABCD 下应为 [0x40B0, 0x0000]", () =>
        {
            var regs = ModbusValueCodec.Encode(5.5, Var("V", "Float"), WordOrder.Abcd)!;

            TestHelpers.AssertEq(0x40B0, regs[0], "高字在前");
            TestHelpers.AssertEq(0x0000, regs[1], "低字在后");
        });

        run("写入编码:BADC 交换寄存器内字节", () =>
        {
            var regs = ModbusValueCodec.Encode(5.5, Var("V", "Float"), WordOrder.Badc)!;

            // 大端 40 B0 00 00 → 字节交换 B0 40 00 00
            TestHelpers.AssertEq(0xB040, regs[0], "首寄存器字节应交换");
        });

        run("写入编码:DCBA 字节与字都交换", () =>
        {
            var regs = ModbusValueCodec.Encode(5.5, Var("V", "Float"), WordOrder.Dcba)!;

            // 大端 40 B0 00 00 → 字交换 00 00 40 B0 → 再字节交换 00 00 B0 40
            TestHelpers.AssertEq(0x0000, regs[0], "首寄存器应为 0");
            TestHelpers.AssertEq(0xB040, regs[1], "次寄存器应为 B040");
        });

        run("写入编码:Short 0x1234 在 BADC 下高低字节互换", () =>
        {
            var regs = ModbusValueCodec.Encode(0x1234, Var("V", "Short"), WordOrder.Abcd)!;
            TestHelpers.AssertEq(0x1234, regs[0], "ABCD 应原样");

            var swapped = ModbusValueCodec.Encode(0x1234, Var("V", "Short"), WordOrder.Badc)!;
            TestHelpers.AssertEq(0x3412, swapped[0], "BADC 应交换字节");
        });

        run("写入编码:Int32 1000 在 CDAB 下可编码为 2 寄存器", () =>
        {
            var regs = ModbusValueCodec.Encode(1000, Var("V", "Int"), WordOrder.Cdab);

            TestHelpers.AssertTrue(regs is not null && regs.Length == 2, "Int 应占 2 寄存器");

            // 1000 = 0x000003E8 → 大端 00 00 03 E8 → 字交换 03 E8 00 00
            TestHelpers.AssertEq(0x03E8, regs![0], "低字在前");
            TestHelpers.AssertEq(0x0000, regs[1], "高字在后");
        });

        run("写入编码:64 位类型明确不支持", () =>
        {
            var n = ModbusValueCodec.RegisterCountForWrite("Double", out var reason);

            TestHelpers.AssertTrue(n < 0, "应标记不支持");
            TestHelpers.AssertTrue(reason.Contains("64 位"), "应说明原因");
        });

        run("写入编码:未知类型不支持", () =>
        {
            TestHelpers.AssertTrue(
                ModbusValueCodec.RegisterCountForWrite("Flout", out _) < 0, "拼错应不支持");
        });
    }

    private sealed class FakeWriter : IDeviceWriter
    {
        public int Calls;
        public string LastName = "";
        public double LastValue;
        public Func<bool>? FailWith;

        public bool WriteVariable(string varName, double value, out string error)
        {
            Calls++;
            LastName = varName;
            LastValue = value;

            if (FailWith is not null && FailWith() == false)
            {
                error = "设备超时";
                return false;
            }

            error = "";
            return true;
        }

        // 占位:让 lambda 里能写 return false
        private static bool False() => false;
    }

    private static CommandGate MakeGate(out FakeWriter writer, bool enabled = true)
    {
        writer = new FakeWriter();

        var device = new DeviceConfig { DeviceName = "D1" };

        var g = new GroupConfig { GroupName = "设定值", Start = "1.400101", Length = 10 };

        g.VarList.Add(new VariableConfig { VarName = "W1", VarType = "Float", Start = "1.400101", Scale = 1f });
        g.VarList.Add(new VariableConfig { VarName = "W2", VarType = "Short", Start = "1.400103", Scale = 1f });
        g.VarList.Add(new VariableConfig { VarName = "R1", VarType = "Double", Start = "1.400105", Scale = 1f });

        device.GroupList.Add(g);

        var gate = new CommandGate { Enabled = enabled, Writer = writer, MinInterval = TimeSpan.Zero };

        gate.BuildWritables(
            device,
            v => v.VarType != "Double",
            v => "64 位类型暂不支持写入");

        return gate;
    }

    private static void RunGate(Action<string, Action> run)
    {
        run("写入闸门:未启用时拒绝,且不下发", () =>
        {
            var gate = MakeGate(out var w, enabled: false);

            var r = gate.Write("W1", 1.0);

            TestHelpers.AssertTrue(!r.Success, "应被拒");
            TestHelpers.AssertTrue(r.Verdict == WriteVerdict.Disabled, "原因应为未启用");
            TestHelpers.AssertEq(0, w.Calls, "不应下发到设备");
        });

        run("写入闸门:启用后正常下发", () =>
        {
            var gate = MakeGate(out var w);

            var r = gate.Write("W1", 5.5);

            TestHelpers.AssertTrue(r.Success, "应成功,实际: " + r.Message);
            TestHelpers.AssertEq(1, w.Calls, "应下发 1 次");
            TestHelpers.AssertStr("W1", w.LastName, "变量名");
            TestHelpers.AssertTrue(Math.Abs(w.LastValue - 5.5) < 1e-9, "值应为 5.5");
        });

        run("写入闸门:不存在的变量被拒", () =>
        {
            var gate = MakeGate(out var w);

            var r = gate.Write("不存在", 1);

            TestHelpers.AssertTrue(r.Verdict == WriteVerdict.UnknownVar, "应为未知变量");
            TestHelpers.AssertEq(0, w.Calls, "不应下发");
        });

        run("写入闸门:不可写点被拒并给出原因", () =>
        {
            var gate = MakeGate(out var w);

            var r = gate.Write("R1", 1);

            TestHelpers.AssertTrue(r.Verdict == WriteVerdict.NotWritable, "应为不可写");
            TestHelpers.AssertTrue(r.Message.Contains("64 位"), "应说明原因,实际: " + r.Message);
            TestHelpers.AssertEq(0, w.Calls, "不应下发");
        });

        run("写入闸门:NaN 被拒", () =>
        {
            var gate = MakeGate(out var w);

            var r = gate.Write("W1", double.NaN);

            TestHelpers.AssertTrue(r.Verdict == WriteVerdict.BadValue, "应为非法值");
            TestHelpers.AssertEq(0, w.Calls, "不应下发");
        });

        run("写入闸门:限速生效(连点只下发一次)", () =>
        {
            var gate = MakeGate(out var w);
            gate.MinInterval = TimeSpan.FromMilliseconds(500);

            var r1 = gate.Write("W1", 1.0);
            var r2 = gate.Write("W1", 2.0);

            TestHelpers.AssertTrue(r1.Success, "第一次应成功");
            TestHelpers.AssertTrue(!r2.Success, "第二次应被限速拒");
            TestHelpers.AssertEq(1, w.Calls, "只应下发 1 次");
        });

        run("写入闸门:设备失败时返回设备错误", () =>
        {
            var gate = MakeGate(out var w);
            w.FailWith = () => false;

            var r = gate.Write("W1", 1.0);

            TestHelpers.AssertTrue(r.Verdict == WriteVerdict.DeviceError, "应为设备错误");
            TestHelpers.AssertTrue(r.Message.Contains("设备超时"), "应带设备原因");
        });

        run("写入闸门:审计记录成功与失败", () =>
        {
            var gate = MakeGate(out _);

            gate.Write("W1", 1.0);

            TestHelpers.AssertTrue(gate.Audit.Count >= 1, "应记录审计");
            TestHelpers.AssertStr("W1", gate.Audit[^1].TagName, "审计变量名");
        });

        run("写入地址:必须写绝对地址,不能只写组内相对偏移", () =>
        {
            // 组基准 100(1.400101),变量 Start=100 → 相对偏移 0。
            // 若把"相对偏移"当地址下发,就会打到寄存器 0(实时值区)——
            // 这是实测踩到的严重 bug:设定值写进了实时测量寄存器。
            var sim = new ModbusSlaveSimulator ();

            var device = new DeviceConfig
            {
                DeviceName = "D1",
                ProtocolKey = "ModbusTCP",
                WordOrder = WordOrder.Cdab,
                Connection = new ConnectionConfig
                {
                    Kind = ConnectionKind.Tcp,
                    IpAddress = "127.0.0.1",
                    Port = sim.Port
                }
            };

            var g = new GroupConfig { GroupName = "设定值", Start = "1.400101", Length = 10, IsWritable = true };

            g.VarList.Add (new VariableConfig
            {
                VarName = "SP1",
                VarType = "Float",
                Start = "100",
                Scale = 1f
            });

            device.GroupList.Add (g);

            var desc = KyjScada.Domain.Protocol.ProtocolRegistry.Find ("ModbusTCP")!;
            var svc = new ModbusAcquisitionService (device, desc);

            try
            {
                sim.SetRegister (0, 0x1234);
                sim.SetRegister (100, 0x0000);
                sim.SetRegister (101, 0x0000);

                                var ok = svc.WriteVariable ("SP1", 12.34, out var err);
                TestHelpers.AssertTrue (ok, "写入应成功,原因: " + err);

                // 12.34f 位模式 0x414570A4;CDAB 下寄存器 = [0x70A4, 0x4145]
                TestHelpers.AssertEq (0x70A4, (int)sim.GetRegister (100), "寄存器 100(绝对地址)应被写入");
                TestHelpers.AssertEq (0x4145, (int)sim.GetRegister (101), "寄存器 101 应被写入");

                // 寄存器 0 必须没被碰过
                TestHelpers.AssertEq (0x1234, (int)sim.GetRegister (0),
                    "寄存器 0(实时值区)不能被误写");
            }
            finally
            {
                svc.Dispose ();
                sim.Dispose ();
            }
        });

        run("写入地址:Bool 写线圈也走绝对地址", () =>
        {
            var sim = new ModbusSlaveSimulator ();

            var device = new DeviceConfig
            {
                DeviceName = "D1",
                ProtocolKey = "ModbusTCP",
                Connection = new ConnectionConfig
                {
                    Kind = ConnectionKind.Tcp,
                    IpAddress = "127.0.0.1",
                    Port = sim.Port
                }
            };

            // 线圈区基准 5,变量 Start=5 → 相对 0,绝对 5
            var g = new GroupConfig { GroupName = "设定值", Start = "1.000005", Length = 10, IsWritable = true };

            g.VarList.Add (new VariableConfig { VarName = "C1", VarType = "Bool", Start = "5", Scale = 1f });

            device.GroupList.Add (g);

            var desc = KyjScada.Domain.Protocol.ProtocolRegistry.Find ("ModbusTCP")!;
            var svc = new ModbusAcquisitionService (device, desc);

            try
            {
                TestHelpers.AssertTrue (svc.WriteVariable ("C1", 1, out var err), "应写入,原因: " + err);

                TestHelpers.AssertTrue (sim.GetCoil (5), "线圈 5 应为 ON");
                TestHelpers.AssertTrue (!sim.GetCoil (0), "线圈 0 不能被误写");
            }
            finally
            {
                svc.Dispose ();
                sim.Dispose ();
            }
        });

        run("写入可行性:输入寄存器区不可写", () =>
        {
            var v = new VariableConfig { VarName = "X", VarType = "Float", Start = "1.300001" };

            TestHelpers.AssertTrue(
                !ModbusAcquisitionService.CanWrite("1.300001", v, out var reason), "3xxxx 应不可写");
            TestHelpers.AssertTrue(reason.Contains("只读"), "应说明只读,实际: " + reason);
        });

        run("写入可行性:保持寄存器区的 Float 可写", () =>
        {
            var v = new VariableConfig { VarName = "X", VarType = "Float", Start = "1.400101" };

            TestHelpers.AssertTrue(
                ModbusAcquisitionService.CanWrite("1.400101", v, out var reason), "应可写,原因: " + reason);
        });

        run("写入可行性:线圈区 Bool 可写", () =>
        {
            var v = new VariableConfig { VarName = "X", VarType = "Bool", Start = "0" };

            TestHelpers.AssertTrue(
                ModbusAcquisitionService.CanWrite("1.000001", v, out _), "线圈应可写");
        });

        run("写入可行性:Double 不可写", () =>
        {
            var v = new VariableConfig { VarName = "X", VarType = "Double", Start = "1.400101" };

            TestHelpers.AssertTrue(
                !ModbusAcquisitionService.CanWrite("1.400101", v, out var reason), "应为不可写");
            TestHelpers.AssertTrue(reason.Contains("64 位"), "应说明原因");
        });
    }
}