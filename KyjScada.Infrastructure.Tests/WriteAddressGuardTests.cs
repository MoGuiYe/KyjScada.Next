using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Domain.Protocol;
using KyjScada.Infrastructure.Acquisition;
using KyjScada.Infrastructure.Modbus;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 写入地址越界的回归用例。
///
/// 背景:写入路径以前一次都不查地址范围 ——
///   · `(ushort)abs` 会**静默回绕**:组基准 65500、变量绝对偏移 65600 时会被写成地址 64,
///     完全是另一个点位;
///   · 起始地址合法也不代表整段合法:32 位值写在 65535 上会跨到地址 0,后半个字落到区首。
/// 读路径有 CheckAddress 拦这类越界,写路径没有 ——
/// 读错点只是错值,写错点是动错设备,性质不同,所以这里必须钉住:
/// **拒绝 + 一个字节都不许发出去**(用假传输层直接数发送次数)。
/// </summary>
public static class WriteAddressGuardTests
{
    public static void RunAll (Action<string, Action> run)
    {
        run ("写入地址:超出 16 位空间必须拒绝(不能强转回绕到别的点位)", () =>
        {
            var transport = new RecordingTransport ();

            // 组基准 1.400101 → 偏移 100;变量 Start=70000 → 绝对地址 70000(> 65535)
            using var svc = Build (transport, "SP_HI", "70000");

            var ok = svc.WriteVariable ("SP_HI", 1.0, out var error);

            TestHelpers.AssertTrue (!ok, "地址越界必须拒绝写入");
            TestHelpers.AssertTrue (error.Contains ("地址越界"),
                "错误要指明越界(现场要看得懂),实际: " + error);

            // 关键:拒绝之后**一个字节都不能发出去**
            // (强转回绕会把值写到 70000-65536=4464 那个点位上去)
            TestHelpers.AssertEq (0, transport.WriteCalls, "越界时不能向设备下发任何帧");
        });

        run ("写入地址:32 位值跨出地址空间必须拒绝(后半截会绕回区首)", () =>
        {
            var transport = new RecordingTransport ();

            // 绝对地址 65535(Float 占 2 个寄存器 → 65535+2 > 65536)
            using var svc = Build (transport, "SP_EDGE", "65535");

            var ok = svc.WriteVariable ("SP_EDGE", 1.0, out var error);

            TestHelpers.AssertTrue (!ok, "跨出地址空间必须拒绝写入");
            TestHelpers.AssertTrue (error.Contains ("跨出地址空间"),
                "错误要指明跨段,实际: " + error);

            TestHelpers.AssertEq (0, transport.WriteCalls, "跨段时不能向设备下发任何帧");
        });

        run ("写入地址:合法地址照常下发(证明上面两条不是「一律拒绝」)", () =>
        {
            var transport = new RecordingTransport ();

            // 组基准偏移 100,变量 Start=100 → 绝对地址 100,Float 两寄存器都在范围内
            using var svc = Build (transport, "SP_OK", "100");

            var ok = svc.WriteVariable ("SP_OK", 12.5, out var error);

            TestHelpers.AssertTrue (ok, "合法地址应写入成功,原因: " + error);
            TestHelpers.AssertEq (1, transport.WriteCalls, "应恰好下发一帧");
            TestHelpers.AssertEq (100, transport.LastAddress, "应发到绝对地址 100");
        });
    }

    private static ModbusAcquisitionService Build (IModbusTransport transport, string varName, string varStart)
    {
        var device = new DeviceConfig
        {
            DeviceName = "D1",
            ProtocolKey = "ModbusTCP",
            WordOrder = WordOrder.Cdab,
            Connection = new ConnectionConfig { Kind = ConnectionKind.Tcp, IpAddress = "127.0.0.1", Port = 502 }
        };

        var group = new GroupConfig
        {
            GroupName = "设定值",
            Start = "1.400101",
            Length = 10,
            IsWritable = true
        };

        group.VarList.Add (new VariableConfig
        {
            VarName = varName,
            VarType = "Float",
            Start = varStart,
            Scale = 1f
        });

        device.GroupList.Add (group);

        return new ModbusAcquisitionService (device, ProtocolRegistry.Find ("ModbusTCP")!, transport);
    }

    /// <summary>假传输层:只数"到底发了几帧、发到哪个地址",不碰网络。</summary>
    private sealed class RecordingTransport : IModbusTransport
    {
        public int WriteCalls;

        public ushort LastAddress;

        public bool IsConnected => true;

        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds (1);

        public void Connect ()
        {
        }

        public void Disconnect ()
        {
        }

        public ushort[] ReadHoldingRegisters (ushort startAddress, ushort quantity) => Array.Empty<ushort> ();

        public ushort[] ReadInputRegisters (ushort startAddress, ushort quantity) => Array.Empty<ushort> ();

        public bool[] ReadCoils (ushort startAddress, ushort quantity) => Array.Empty<bool> ();

        public void WriteCoil (ushort address, bool value)
        {
            WriteCalls++;
            LastAddress = address;
        }

        public void WriteRegister (ushort address, ushort value)
        {
            WriteCalls++;
            LastAddress = address;
        }

        public void WriteRegisters (ushort startAddress, ushort[] values)
        {
            WriteCalls++;
            LastAddress = startAddress;
        }

        public void Dispose ()
        {
        }
    }
}
