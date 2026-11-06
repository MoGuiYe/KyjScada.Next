using KyjScada.Infrastructure.Modbus;

namespace KyjScada.Infrastructure.Tests;

using static KyjScada.Infrastructure.Tests.TestHelpers;

public static class ModbusTcpClientTests
{
    public static void RunAll (Action<string, Action> run)
    {
        run ("WriteBool 真的写进从站:ON", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var client = new ModbusTcpClient ("127.0.0.1", sim.Port, 1);
            try
            {
                client.Connect ();
                AssertTrue (client.IsConnected, "应已连接");
                AssertTrue (!sim.GetCoil (10), "初始应为 OFF");
                client.WriteBool (10, true);
                AssertTrue (sim.GetCoil (10), "写入后从站线圈必须为 ON");
            }
            finally
            {
                client.Dispose ();
                sim.Dispose ();
            }
        });
        run ("WriteBool 真的写进从站:OFF", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var client = new ModbusTcpClient ("127.0.0.1", sim.Port, 1);
            try
            {
                client.Connect ();
                client.WriteBool (10, true);
                AssertTrue (sim.GetCoil (10), "先置 ON");
                client.WriteBool (10, false);
                AssertTrue (!sim.GetCoil (10), "再置 OFF 必须生效");
            }
            finally
            {
                client.Dispose ();
                sim.Dispose ();
            }
        });

        run ("点动:连续开关 20 次全部生效", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var client = new ModbusTcpClient ("127.0.0.1", sim.Port, 1);
            try
            {
                client.Connect ();
                var i = 0;
                while (i < 20)
                {
                    client.WriteBool (5, true);
                    AssertTrue (sim.GetCoil (5), "第 " + i + " 次 ON 未生效");
                    client.WriteBool (5, false);
                    AssertTrue (!sim.GetCoil (5), "第 " + i + " 次 OFF 未生效");
                    i++;
                }
            }
            finally
            {
                client.Dispose ();
                sim.Dispose ();
            }
        });

        run ("读保持寄存器", () =>
        {
            var sim = new ModbusSlaveSimulator ();
            var client = new ModbusTcpClient ("127.0.0.1", sim.Port, 1);
            try
            {
                sim.SetRegister (0, 1234);
                sim.SetRegister (1, 5678);
                client.Connect ();
                var regs = client.ReadHoldingRegisters (0, 2);
                AssertEq (2, regs.Length, "应读到 2 个寄存器");
                AssertEq (1234, (int)regs[0], "寄存器 0");
                AssertEq (5678, (int)regs[1], "寄存器 1");
            }
            finally
            {
                client.Dispose ();
                sim.Dispose ();
            }
        });
    }
}

