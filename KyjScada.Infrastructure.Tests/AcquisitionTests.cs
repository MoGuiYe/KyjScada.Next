using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Domain.Protocol;
using KyjScada.Infrastructure.Acquisition;
using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

using static KyjScada.Infrastructure.Tests.TestHelpers;

public static class AcquisitionTests
{
    public static void RunAll (Action<string, Action> run)
    {

        run ("采集 Float 变量:值正确", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var svc = BuildService (sim, "Float", 2, "40001");
            var bus = new ValueBus ();
            try
            {
                sim.SetRegister (0, FloatHi (12.5f));
                sim.SetRegister (1, FloatLo (12.5f));

                var ok = svc.CollectOnce (bus, DateTime.Now);
                AssertEq (1, ok, "应成功采集 1 个");

                var snap = bus.GetSnapshot ();
                var tv = snap["T1"];
                AssertTrue (tv != null, "总线上应有 T1");
                AssertEq (0, (int)tv.Quality, "质量应为 Good");
                AssertTrue (Math.Abs (tv.AsDouble ().Value - 12.5) < 0.001, "值应为 12.5");
            }
            finally { svc.Dispose (); sim.Dispose (); }
        });

        run ("读失败写 NotCollected,绝不写 0", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var svc = BuildService (sim, "Float", 2, "40001");
            var bus = new ValueBus ();
            try
            {
                sim.SetRegister (0, FloatHi (12.5f));
                sim.SetRegister (1, FloatLo (12.5f));
                svc.CollectOnce (bus, DateTime.Now);

                // 只 Dispose 监听器不会断开已建立的 TCP 连接,必须先主动断开客户端
                svc.Disconnect ();
                sim.Dispose ();
                svc.CollectOnce (bus, DateTime.Now);

                var snap = bus.GetSnapshot ();
                var tv = snap["T1"];
                AssertTrue (tv.Value == null, "读失败时值必须为 null,不能是 0");
                AssertEq (3, (int)tv.Quality, "质量应为 NotCollected");
            }
            finally { svc.Dispose (); }
        });

        run ("Int32 变量采集", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var svc = BuildService (sim, "Int32", 2, "40001");
            var bus = new ValueBus ();
            try
            {
                sim.SetRegister (0, 0x0001);
                sim.SetRegister (1, 0x86A0);
                svc.CollectOnce (bus, DateTime.Now);
                var snap = bus.GetSnapshot ();
                AssertTrue (Math.Abs (snap["T1"].AsDouble ().Value - 100000.0) < 1, "值应为 100000");
            }
            finally { svc.Dispose (); sim.Dispose (); }
        });

        run ("缩放系数生效", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var svc = BuildService (sim, "Float", 2, "40001", 2.0f);
            var bus = new ValueBus ();
            try
            {
                sim.SetRegister (0, FloatHi (5.0f));
                sim.SetRegister (1, FloatLo (5.0f));
                svc.CollectOnce (bus, DateTime.Now);
                var snap = bus.GetSnapshot ();
                AssertTrue (Math.Abs (snap["T1"].AsDouble ().Value - 10.0) < 0.001, "5.0 x 2.0 应为 10.0");
            }
            finally { svc.Dispose (); sim.Dispose (); }
        });

        run ("不支持协议显式报错", () =>
        {
            var dev = new DeviceConfig { DeviceName = "D", ProtocolKey = "Siemens" };
            var desc = ProtocolRegistry.Find ("Siemens") ?? ProtocolRegistry.All ().First ();
            var svc = new ModbusAcquisitionService (dev, desc);
            try
            {
                var thrown = false;
                try { svc.CollectOnce (new ValueBus (), DateTime.Now); }
                catch (Exception) { thrown = true; }
                AssertTrue (thrown, "未实现协议必须抛异常,不能静默");
            }
            finally { svc.Dispose (); }
        });

        run ("缩放系数生效:Scale=0.1", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var svc = BuildService (sim, "Short", 1, "40001", 0.1f);
            var bus = new ValueBus ();
            try
            {
                sim.SetRegister (0, 1234);
                svc.CollectOnce (bus, DateTime.Now);
                var snap = bus.GetSnapshot ();
                AssertTrue (Math.Abs (snap["T1"].AsDouble ().Value - 123.4) < 0.01, "1234*0.1=123.4");
            }
            finally { svc.Dispose (); sim.Dispose (); }
        });

        run ("缩放系数生效", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var svc = BuildService (sim, "Short", 1, "40001", 0.1f);
            var bus = new ValueBus ();
            try
            {
                sim.SetRegister (0, 250);
                svc.CollectOnce (bus, DateTime.Now);
                var snap = bus.GetSnapshot ();
                AssertTrue (Math.Abs (snap["T1"].AsDouble ().Value - 25.0) < 0.001, "250 * 0.1 = 25");
            }
            finally { svc.Dispose (); sim.Dispose (); }
        });

        run ("缩放系数为 0 时按 1 处理,不产生全零", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var svc = BuildService (sim, "Short", 1, "40001", 0f);
            var bus = new ValueBus ();
            try
            {
                sim.SetRegister (0, 250);
                svc.CollectOnce (bus, DateTime.Now);
                var snap = bus.GetSnapshot ();
                AssertTrue (Math.Abs (snap["T1"].AsDouble ().Value - 250.0) < 0.001, "Scale=0 必须当作 1");
            }
            finally { svc.Dispose (); sim.Dispose (); }
        });

        run ("偏移量生效", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var svc = BuildService (sim, "Short", 1, "40001", 1f, 5f);
            var bus = new ValueBus ();
            try
            {
                sim.SetRegister (0, 100);
                svc.CollectOnce (bus, DateTime.Now);
                var snap = bus.GetSnapshot ();
                AssertTrue (Math.Abs (snap["T1"].AsDouble ().Value - 105.0) < 0.001, "100 + 5 = 105");
            }
            finally { svc.Dispose (); sim.Dispose (); }
        });

        run ("协议未实现时抛异常,不静默返回", () =>
        {
            var device = new DeviceConfig { DeviceName = "D1", ProtocolKey = "Siemens" };
            var desc = ProtocolRegistry.Find ("Siemens");
            var svc = new ModbusAcquisitionService (device, desc);
            try
            {
                var bus = new ValueBus ();
                var thrown = false;
                try { svc.CollectOnce (bus, DateTime.Now); }
                catch (Exception) { thrown = true; }
                AssertTrue (thrown, "未实现协议必须抛异常");
            }
            finally { svc.Dispose (); }
        });

        run ("组基准非零:变量绝对地址 40101 落在基准 400101 的组内", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var svc = BuildService (sim, "Float", "1.40101", "1.400101", 48);
            var bus = new ValueBus ();
            try
            {
                sim.SetRegister (100, FloatHi (7.25f));
                sim.SetRegister (101, FloatLo (7.25f));

                var ok = svc.CollectOnce (bus, DateTime.Now);
                AssertEq (1, ok, "设定值组应能读到 1 个");

                var tv = bus.GetSnapshot ()["T1"];
                AssertTrue (tv.Value != null, "值不应为 null(越界会返回 null)");
                AssertTrue (Math.Abs (tv.AsDouble ().Value - 7.25) < 0.001, "应为 7.25");
            }
            finally { svc.Dispose (); sim.Dispose (); }
        });

        run ("测试组:Double 占 4 个寄存器且值正确", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var svc = BuildService (sim, "Double", "1.40210", "1.400201", 13);
            var bus = new ValueBus ();
            try
            {
                var hi = DoubleWords (3.5);
                sim.SetRegister (209, hi[0]);
                sim.SetRegister (210, hi[1]);
                sim.SetRegister (211, hi[2]);
                sim.SetRegister (212, hi[3]);

                var ok = svc.CollectOnce (bus, DateTime.Now);
                AssertEq (1, ok, "Double 应能读到");

                var tv = bus.GetSnapshot ()["T1"];
                AssertTrue (tv.Value != null, "值不应为 null");
                AssertTrue (Math.Abs (tv.AsDouble ().Value - 3.5) < 0.0001, "应为 3.5");
            }
            finally { svc.Dispose (); sim.Dispose (); }
        });

        run ("Byte 类型:取寄存器高字节而非整个 16 位", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var svc = BuildService (sim, "Byte", "1.40201", "1.400201", 13);
            var bus = new ValueBus ();
            try
            {
                sim.SetRegister (200, 0x0500);
                var ok = svc.CollectOnce (bus, DateTime.Now);
                AssertEq (1, ok, "Byte 应能读到");

                var tv = bus.GetSnapshot ()["T1"];
                AssertTrue (tv.Value != null, "值不应为 null");
                AssertTrue (Math.Abs (tv.AsDouble ().Value - 5.0) < 0.001, "高字节应为 5,实际 " + tv.AsDouble ());
            }
            finally { svc.Dispose (); sim.Dispose (); }
        });
        run ("字节序 CDAB:模拟器实测 0x0000/0x40B0 应读出 5.5", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var svc = BuildService (sim, "Float", "1.40001", "1.400001", 10, 1f, 0f, WordOrder.Cdab);
            var bus = new ValueBus ();
            try
            {
                // 模拟器地址 0 存 0x0000,地址 1 存 0x40B0 -> CDAB 拼成 0x40B00000 = 5.5
                sim.SetRegister (0, 0x0000);
                sim.SetRegister (1, 0x40B0);

                var ok = svc.CollectOnce (bus, DateTime.Now);
                AssertEq (1, ok, "应读到 1 个");

                var tv = bus.GetSnapshot ()["T1"];
                AssertTrue (tv.Value != null, "值不应为 null");
                AssertTrue (Math.Abs (tv.AsDouble ().Value - 5.5) < 0.001, "CDAB 应为 5.5,实际 " + tv.AsDouble ());
            }
            finally { svc.Dispose (); sim.Dispose (); }
        });

        run ("字节序 ABCD:同样数据应读出 0(证明两种序确实不同)", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var svc = BuildService (sim, "Float", "1.40001", "1.400001", 10, 1f, 0f, WordOrder.Abcd);
            var bus = new ValueBus ();
            try
            {
                sim.SetRegister (0, 0x0000);
                sim.SetRegister (1, 0x40B0);

                var ok = svc.CollectOnce (bus, DateTime.Now);
                AssertEq (1, ok, "应读到 1 个");

                var tv = bus.GetSnapshot ()["T1"];
                AssertTrue (tv.Value != null, "值不应为 null");
                AssertTrue (Math.Abs (tv.AsDouble ().Value - 0.0) < 0.001, "ABCD 应为 0,实际 " + tv.AsDouble ());
            }
            finally { svc.Dispose (); sim.Dispose (); }
        });
        run ("字节序往返保真:DataFormat=2 解析为 CDAB 并写回 2", () =>
        {
            var json = "[{\"ProjectName\":\"P\",\"ModbusTCPList\":[{\"DeviceName\":\"D\",\"DataFormat\":\"2\",\"IpAddress\":\"127.0.0.1\",\"Port\":502,\"GroupList\":[]}]}]";

            var projects = KyjScada.Domain.Config.SettingsJsonCodec.Parse (json);
            var dev = projects[0].AllDevices ().First ();

            TestHelpers.AssertTrue (dev.WordOrder == KyjScada.Domain.Config.WordOrder.Cdab, "DataFormat=2 应为 CDAB");

            var back = KyjScada.Domain.Config.SettingsJsonCodec.ToJson (projects, false);
            TestHelpers.AssertTrue (back.Contains ("\"DataFormat\":\"2\"") || back.Contains ("\"DataFormat\": 2"), "写回应保留 2");
        });

        run ("字节序:DataFormat 缺省按 ABCD", () =>
        {
            var json = "[{\"ProjectName\":\"P\",\"ModbusTCPList\":[{\"DeviceName\":\"D\",\"IpAddress\":\"127.0.0.1\",\"Port\":502,\"GroupList\":[]}]}]";
            var projects = KyjScada.Domain.Config.SettingsJsonCodec.Parse (json);
            var dev = projects[0].AllDevices ().First ();
            TestHelpers.AssertTrue (dev.WordOrder == KyjScada.Domain.Config.WordOrder.Abcd, "缺省应为 ABCD");
        });

        run ("位地址:Start=200.0(第 0 位)按单 bit 读,不当整寄存器", () =>
        {
            // 以前 bit 用 0 当"没有位号"的哨兵,`200.0` 这种点会被当成整寄存器读,
            // 值永远是整字(0x0003)而不是 bit0(1)。这是真实配置里最常见的写法。
            var sim = new ModbusSlaveSimulator ();
            // 组基准必须盖住寄存器 200:组起始 1.400201 → 偏移 200,长度 10
            var svc = BuildService (sim, "Short", "200.0", "1.400201", 10);
            var bus = new ValueBus ();
            try
            {
                sim.SetRegister (200, 0x0003); // bit0=1, bit1=1, 其余 0

                var ok = svc.CollectOnce (bus, DateTime.Now);
                AssertEq (1, ok, "位点应能读到");

                var tv = bus.GetSnapshot ()["T1"];
                AssertTrue (tv?.Value != null, "值不应为 null");
                AssertTrue (Math.Abs ((tv?.AsDouble () ?? double.NaN) - 1.0) < 1e-9,
                    "bit0 应为 1(整字会是 3),实际 " + tv.AsDouble ());

                // 换个值:0x0002 → bit0 = 0,bit1 = 1
                sim.SetRegister (200, 0x0002);

                ok = svc.CollectOnce (bus, DateTime.Now);
                AssertEq (1, ok, "第二次也应读到");

                tv = bus.GetSnapshot ()["T1"];
                AssertTrue (Math.Abs ((tv?.AsDouble () ?? double.NaN) - 0.0) < 1e-9,
                    "0x0002 的 bit0 应为 0,实际 " + tv?.AsDouble ());
            }
            finally { svc.Dispose (); sim.Dispose (); }
        });

        run ("字节序:四种序都能把自己编码的寄存器读回原值(往返)", () =>
        {
            foreach (var order in new[] { WordOrder.Abcd, WordOrder.Badc, WordOrder.Cdab, WordOrder.Dcba })
            {
                var sim = new ModbusSlaveSimulator ();
                var svc = BuildService (sim, "Float", "1.40001", "1.400001", 10, 1f, 0f, order);
                var bus = new ValueBus ();
                try
                {
                    var regs = RegsForFloat (5.5f, order);
                    sim.SetRegister (0, regs[0]);
                    sim.SetRegister (1, regs[1]);

                    var ok = svc.CollectOnce (bus, DateTime.Now);
                    AssertEq (1, ok, order + " 应读到 1 个");

                    var tv = bus.GetSnapshot ()["T1"];
                    AssertTrue (tv?.Value != null, order + " 值不应为 null");
                    AssertTrue (Math.Abs ((tv?.AsDouble () ?? double.NaN) - 5.5) < 0.001,
                        order + " 往返应得 5.5,实际 " + tv?.AsDouble ());
                }
                finally { svc.Dispose (); sim.Dispose (); }
            }
        });

        run ("地址越界:65535 起的 Float 跨出 16 位空间时明确报错,不回绕", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            // 组起始 465536 = 偏移 65535(16 位空间的最后一个寄存器),长度 2 已经越界
            var svc = BuildService (sim, "Float", "65535", "1.465536", 2);
            var bus = new ValueBus ();
            try
            {
                // (ushort)65536 会回绕成 0 —— "读到了别的点位"比读失败危险得多
                sim.SetRegister (0, FloatHi (5.5f));
                sim.SetRegister (1, FloatLo (5.5f));

                var ok = svc.CollectOnce (bus, DateTime.Now);

                AssertEq (0, ok, "越界组不应采到任何点");

                var tv = bus.GetSnapshot ()["T1"];
                AssertTrue (tv?.Value == null,
                    "越界点必须是 null,绝不能是回绕地址上的值 " + tv?.AsDouble ());
                AssertTrue (tv is null || (int)tv.Quality != 0, "越界点质量不能是 Good");

                AssertTrue (!string.IsNullOrEmpty (svc.LastError) && svc.LastError!.Contains ("越界"),
                    "应明确报告地址越界,实际 LastError=" + svc.LastError);
            }
            finally { svc.Dispose (); sim.Dispose (); }
        });

        run ("地址越界:解析不出来的组起始(70000)只报未采集,不抛异常", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var svc = BuildService (sim, "Short", "1.70000", "1.70000", 10);
            var bus = new ValueBus ();
            try
            {
                // 组起始 70000 既不是 4xxxx/3xxxx/1xxxx 任何一段的合法起始,
                // 也不在 0..65535 之内:必须直接判为"这个组不可读",一个点都不采。
                // (绝不能 (ushort)70000 回绕成 4464 去读别人。)
                var ok = svc.CollectOnce (bus, DateTime.Now);

                AssertEq (0, ok, "组起始非法时不应采到任何点");

                var tv = bus.GetSnapshot ()["T1"];
                AssertTrue (tv?.Value == null, "不应把任何值当成这个点");
                AssertTrue (tv is null || (int)tv.Quality != 0, "质量应为未采集");
            }
            finally { svc.Dispose (); sim.Dispose (); }
        });
    }

    /// <summary>
    /// 按字序把 float 编成两个寄存器(读入侧的逆变换,与 ModbusAcquisitionService.ToBytes 对称)。
    /// 大端字对 + 视字序做整字反转 / 字节交换。
    /// </summary>
    private static ushort[] RegsForFloat (float v, WordOrder order)
    {
        var hi = FloatHi (v);
        var lo = FloatLo (v);

        var regs = new[] { hi, lo };

        if (order == WordOrder.Cdab || order == WordOrder.Dcba) Array.Reverse (regs);

        if (order == WordOrder.Badc || order == WordOrder.Dcba)
        {
            for (var i = 0; i < regs.Length; i++)
            {
                regs[i] = (ushort)((regs[i] << 8) | (regs[i] >> 8));
            }
        }

        return regs;
    }

    private static ModbusAcquisitionService BuildService (ModbusSlaveSimulator sim, string varType, int regCount, string varAddress, float scale = 1f, float offset = 0f)
    {
        return BuildService (sim, varType, varAddress, "1.400001", 10, scale, offset);
    }

    /// <summary>可指定组基准和变量绝对 Start,用于验证组基准非零时的相对偏移。</summary>
    private static ModbusAcquisitionService BuildService (ModbusSlaveSimulator sim, string varType, string varStart, string groupStart, int groupLength, float scale = 1f, float offset = 0f, WordOrder order = WordOrder.Abcd)
    {
        var device = new DeviceConfig
        {
            DeviceName = "D1",
            ProtocolKey = "ModbusTCP",
            WordOrder = order,
            Connection = new ConnectionConfig { Kind = ConnectionKind.Tcp, IpAddress = "127.0.0.1", Port = sim.Port }
        };
        var group = new GroupConfig { GroupName = "G1", IsActive = true, Start = groupStart, Length = groupLength };
        group.VarList.Add (new VariableConfig { VarName = "T1", VarType = varType, VarAddress = varStart, Start = varStart, Scale = scale, Offset = offset });
        device.GroupList.Add (group);
        var desc = ProtocolRegistry.Find ("ModbusTCP");
        return new ModbusAcquisitionService (device, desc!);
    }
    private static ushort FloatHi (float v)
    {
        var b = BitConverter.GetBytes (v);
        if (BitConverter.IsLittleEndian) Array.Reverse (b);
        return (ushort)((b[0] << 8) | b[1]);
    }

    private static ushort FloatLo (float v)
    {
        var b = BitConverter.GetBytes (v);
        if (BitConverter.IsLittleEndian) Array.Reverse (b);
        return (ushort)((b[2] << 8) | b[3]);
    }

    /// <summary>double 拆成 4 个大端寄存器。</summary>
    private static ushort[] DoubleWords (double v)
    {
        var b = BitConverter.GetBytes (v);
        if (BitConverter.IsLittleEndian) Array.Reverse (b);
        var w = new ushort[4];
        w[0] = (ushort)((b[0] << 8) | b[1]);
        w[1] = (ushort)((b[2] << 8) | b[3]);
        w[2] = (ushort)((b[4] << 8) | b[5]);
        w[3] = (ushort)((b[6] << 8) | b[7]);
        return w;
    }

}

