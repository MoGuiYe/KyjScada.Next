using KyjScada.Domain.Config;
using KyjScada.Infrastructure.Acquisition;
using KyjScada.Infrastructure.Modbus;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// Modbus RTU 帧与 CRC 测试(**纯函数,不需要真实串口**)。
///
/// CRC 算错或字节序错,现场表现是"每帧都校验失败",
/// 极易被误判成接线/波特率问题,所以这部分必须用测试钉死。
/// </summary>
public static class ModbusRtuTests
{
    public static void RunAll(Action<string, Action> run)
    {
        RunCrc(run);
        RunFrame(run);
        RunResponseRead(run);
        RunResponseLength(run);
        RunStationId(run);
        RunFactory(run);
    }

    private static void RunCrc(Action<string, Action> run)
    {
        run("RTU CRC:标准样例 01 03 00 00 00 0A → C5 CD", () =>
        {
            var pdu = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };

            var crc = ModbusCrc16.Compute(pdu);

            // 线上低字节先发:0xC5, 0xCD
            TestHelpers.AssertTrue((crc & 0xFF) == 0xC5,
                "CRC 低字节应为 0xC5,实际 0x" + (crc & 0xFF).ToString("X2"));
            TestHelpers.AssertTrue((crc >> 8) == 0xCD,
                "CRC 高字节应为 0xCD,实际 0x" + (crc >> 8).ToString("X2"));
        });

        run("RTU CRC:空/单字节输入不崩溃", () =>
        {
            TestHelpers.AssertTrue(ModbusCrc16.Compute(Array.Empty<byte>()) == 0xFFFF,
                "空输入应返回初值 0xFFFF");

            var one = ModbusCrc16.Compute(new byte[] { 0x01 });
            TestHelpers.AssertTrue(one != 0, "单字节应有非零 CRC");
        });
    }

    private static void RunFrame(Action<string, Action> run)
    {
        run("RTU 帧:封装后自校验通过", () =>
        {
            var pdu = new byte[] { 0x03, 0x00, 0x00, 0x00, 0x0A };

            var frame = ModbusRtuFrame.Wrap(1, pdu);

            TestHelpers.AssertEq(8, frame.Length, "帧长应为 PDU(5)+3");
            TestHelpers.AssertEq(1, frame[0], "首字节应为从站号");
            TestHelpers.AssertTrue(ModbusRtuFrame.VerifyCrc(frame), "自己包的帧应校验通过");
        });

        run("RTU 帧:拆出 PDU 与原始一致", () =>
        {
            var pdu = new byte[] { 0x04, 0x00, 0x10, 0x00, 0x02 };

            var frame = ModbusRtuFrame.Wrap(2, pdu);
            var back = ModbusRtuFrame.ExtractPdu(frame);

            TestHelpers.AssertEq(pdu.Length, back.Length, "PDU 长度应一致");

            for (int i = 0; i < pdu.Length; i++)
            {
                TestHelpers.AssertEq(pdu[i], back[i], "第 " + i + " 字节应一致");
            }
        });

        run("RTU 帧:改一个字节 CRC 就该失败", () =>
        {
            var pdu = new byte[] { 0x03, 0x00, 0x00, 0x00, 0x0A };

            var frame = ModbusRtuFrame.Wrap(1, pdu);

            frame[3] ^= 0xFF;

            TestHelpers.AssertTrue(!ModbusRtuFrame.VerifyCrc(frame), "篡改后应校验失败");
        });

        run("RTU 帧:过短帧不崩溃且校验失败", () =>
        {
            TestHelpers.AssertTrue(!ModbusRtuFrame.VerifyCrc(Array.Empty<byte>()), "空帧应失败");
            TestHelpers.AssertTrue(!ModbusRtuFrame.VerifyCrc(new byte[] { 1, 2 }), "2 字节应失败");
        });

        run("RTU 帧:不同从站号产出不同帧", () =>
        {
            var pdu = new byte[] { 0x03, 0x00, 0x00, 0x00, 0x01 };

            var f1 = ModbusRtuFrame.Wrap(1, pdu);
            var f2 = ModbusRtuFrame.Wrap(2, pdu);

            TestHelpers.AssertTrue(f1[^2] != f2[^2] || f1[^1] != f2[^1],
                "从站号不同时 CRC 也应不同");
        });
    }

    private static void RunResponseLength(Action<string, Action> run)
    {
        run("RTU 响应长度:读寄存器按字节数推算", () =>
        {
            // 从站号+功能码+字节数+数据+CRC = 3 + 4 + 2
            var len = ModbusRtuFrame.ResponseLength(ModbusFunction.ReadHoldingRegisters, 4);

            TestHelpers.AssertEq(9, len, "2 个寄存器(4 字节)应为 9");
        });

        run("RTU 响应长度:异常帧固定 5", () =>
        {
            // 功能码最高位置 1
            var len = ModbusRtuFrame.ResponseLength((byte)(ModbusFunction.ReadHoldingRegisters | 0x80), 0);

            TestHelpers.AssertEq(5, len, "异常帧应为 5");
        });

        run("RTU 响应长度:写单个固定 8", () =>
        {
            TestHelpers.AssertEq(8,
                ModbusRtuFrame.ResponseLength(ModbusFunction.WriteSingleRegister, 0), "写寄存器应为 8");
            TestHelpers.AssertEq(8,
                ModbusRtuFrame.ResponseLength(ModbusFunction.WriteSingleCoil, 0), "写线圈应为 8");
        });

        run("RTU 响应长度:未知功能码抛异常", () =>
        {
            try
            {
                ModbusRtuFrame.ResponseLength(0x42, 0);
                throw new Exception("应抛异常却没抛");
            }
            catch (ModbusException)
            {
                // 期望
            }
        });
    }

    private static void RunResponseRead(Action<string, Action> run)
    {
        run("RTU 读响应:读类响应按字节数精确读满(回归:曾多要 1 字节导致必超时)", () =>
        {
            // 造一个真实读响应:01 03 04 40B0 0000 + CRC
            var pduPart = new byte[] { 0x03, 0x04, 0x40, 0xB0, 0x00, 0x00 };
            var full = ModbusRtuFrame.Wrap(1, pduPart);

            var cursor = 0;

            var frame = ModbusRtuFrame.ReadResponse(count =>
            {
                var buf = new byte[count];
                Array.Copy(full, cursor, buf, 0, count);
                cursor += count;
                return buf;
            });

            TestHelpers.AssertEq(full.Length, frame.Length, "整帧长度应一致");
            TestHelpers.AssertEq(full.Length, cursor, "应恰好读完,不多不少");
            TestHelpers.AssertTrue(ModbusRtuFrame.VerifyCrc(frame), "读出的帧 CRC 应正确");
        });

        run("RTU 读响应:异常帧按 5 字节读", () =>
        {
            var full = ModbusRtuFrame.Wrap(1, new byte[] { 0x83, 0x02 });

            var cursor = 0;

            var frame = ModbusRtuFrame.ReadResponse(count =>
            {
                var buf = new byte[count];
                Array.Copy(full, cursor, buf, 0, count);
                cursor += count;
                return buf;
            });

            TestHelpers.AssertEq(5, frame.Length, "异常帧应为 5 字节");
            TestHelpers.AssertEq(5, cursor, "应恰好读完");
            TestHelpers.AssertTrue((frame[1] & 0x80) != 0, "功能码最高位应为 1");
        });

        run("RTU 读响应:写应答按 8 字节读", () =>
        {
            var full = ModbusRtuFrame.Wrap(1, new byte[] { 0x06, 0x00, 0x64, 0x12, 0x34 });

            var cursor = 0;

            var frame = ModbusRtuFrame.ReadResponse(count =>
            {
                var buf = new byte[count];
                Array.Copy(full, cursor, buf, 0, count);
                cursor += count;
                return buf;
            });

            TestHelpers.AssertEq(8, frame.Length, "写应答应为 8 字节");
            TestHelpers.AssertEq(8, cursor, "应恰好读完");
        });
    }

    private static void RunStationId(Action<string, Action> run)
    {
        run("从站号:取组起始地址的前缀", () =>
        {
            var d = new DeviceConfig { DeviceName = "D" };
            d.GroupList.Add(new GroupConfig { GroupName = "G", Start = "1.400001", Length = 10 });
            d.GroupList.Add(new GroupConfig { GroupName = "G2", Start = "2.400101", Length = 10 });

            // 取第一个能解析的前缀
            TestHelpers.AssertEq(1, ModbusAcquisitionService.StationIdOf(d), "应为 1");
        });

        run("从站号:前缀为 2 时取 2", () =>
        {
            var d = new DeviceConfig { DeviceName = "D" };
            d.GroupList.Add(new GroupConfig { GroupName = "G", Start = "2.400001", Length = 10 });

            TestHelpers.AssertEq(2, ModbusAcquisitionService.StationIdOf(d), "应为 2");
        });

        run("从站号:没有前缀时默认 1", () =>
        {
            var d = new DeviceConfig { DeviceName = "D" };
            d.GroupList.Add(new GroupConfig { GroupName = "G", Start = "400001", Length = 10 });

            TestHelpers.AssertEq(1, ModbusAcquisitionService.StationIdOf(d), "无点号应为 1");
        });

        run("从站号:没有组时默认 1", () =>
        {
            var d = new DeviceConfig { DeviceName = "D" };

            TestHelpers.AssertEq(1, ModbusAcquisitionService.StationIdOf(d), "无组应为 1");
        });
    }

    private static void RunFactory(Action<string, Action> run)
    {
        run("协议分派:ModbusRTU 现在可采集(串口)", () =>
        {
            var d = new DeviceConfig
            {
                DeviceName = "RTU",
                ProtocolKey = "ModbusRTU",
                Connection = new ConnectionConfig
                {
                    Kind = ConnectionKind.Serial,
                    PortName = "COM_TEST_NOT_EXIST",
                    BaudRate = 9600,
                    Parity = 0,
                    DataBits = 8,
                    StopBits = 1
                }
            };

            d.GroupList.Add(new GroupConfig { GroupName = "G", Start = "1.400001", Length = 10 });

            var plan = AcquisitionFactory.Create(d);

            TestHelpers.AssertTrue(plan.Supported, "ModbusRTU 应支持");
            TestHelpers.AssertTrue(plan.Service is not null, "应创建采集器");

            // 不真去开串口,只验证创建成功并释放
            plan.Service!.Dispose();
        });

        run("协议分派:ModbusRTUOverTCP 已实现", () =>
        {
            TestHelpers.AssertTrue(AcquisitionFactory.IsCollectable("ModbusRTUOverTCP"),
                "RTU over TCP 应可采集");
        });
    }
}