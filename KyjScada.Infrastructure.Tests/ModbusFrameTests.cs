using KyjScada.Infrastructure.Modbus;

namespace KyjScada.Infrastructure.Tests;

using static KyjScada.Infrastructure.Tests.TestHelpers;

public static class ModbusFrameTests
{
    public static void RunAll(Action<string, Action> run)
    {
        run("MBAP 头编码", () =>
        {
            var header = ModbusFrame.BuildMbap(0x1234, 6, 1);

            AssertEq(7, header.Length, "头长度");
            AssertEq(0x12, header[0], "事务号高字节");
            AssertEq(0x34, header[1], "事务号低字节");
            AssertEq(0, header[2], "协议标识高");
            AssertEq(0, header[3], "协议标识低");
            AssertEq(0, header[4], "长度高");
            AssertEq(6, header[5], "长度低");
            AssertEq(1, header[6], "单元号");
        });

        run("写单个线圈:ON 必须编码为 0xFF00", () =>
        {
            var pdu = ModbusFrame.BuildWriteSingleCoil(0x0010, true);

            AssertEq(5, pdu.Length, "PDU 长度");
            AssertEq(0x05, pdu[0], "功能码应为 05");
            AssertEq(0x00, pdu[1], "地址高");
            AssertEq(0x10, pdu[2], "地址低");

            // 关键:ON 不是 0x0001,而是 0xFF00
            AssertEq(0xFF, pdu[3], "ON 值高字节必须是 FF");
            AssertEq(0x00, pdu[4], "ON 值低字节必须是 00");
        });

        run("写单个线圈:OFF 必须编码为 0x0000", () =>
        {
            var pdu = ModbusFrame.BuildWriteSingleCoil(0x0010, false);

            AssertEq(0x05, pdu[0], "功能码");
            AssertEq(0x00, pdu[3], "OFF 值高字节必须是 00");
            AssertEq(0x00, pdu[4], "OFF 值低字节必须是 00");
        });

        run("写单个寄存器:值编码", () =>
        {
            var pdu = ModbusFrame.BuildWriteSingleRegister(0x0004, 0x1234);

            AssertEq(0x06, pdu[0], "功能码应为 06");
            AssertEq(0x12, pdu[3], "值高字节");
            AssertEq(0x34, pdu[4], "值低字节");
        });

        run("读保持寄存器:数量编码", () =>
        {
            var pdu = ModbusFrame.BuildReadHoldingRegisters(0x0000, 10);

            AssertEq(0x03, pdu[0], "功能码应为 03");
            AssertEq(0x00, pdu[3], "数量高");
            AssertEq(10, pdu[4], "数量低");
        });

        run("完整帧拼接", () =>
        {
            var pdu = ModbusFrame.BuildReadHoldingRegisters(0, 2);
            var frame = ModbusFrame.BuildFrame(0x0001, 1, pdu);

            AssertEq(12, frame.Length, "7 头 + 5 PDU = 12");
            AssertEq(0x00, frame[0], "事务号高字节");
            AssertEq(0x01, frame[1], "事务号低字节");
            AssertEq(6, frame[5], "长度字段 = PDU+1 = 6");
            AssertEq(1, frame[6], "单元号");
            AssertEq(0x03, frame[7], "功能码");
        });

        run("解析正常响应", () =>
        {
            // 构造:事务 1,协议 0,长度 5,单元 1,功能 03,字节数 4,两寄存器
            var frame = new byte[]
            {
                0x00, 0x01, 0x00, 0x00, 0x00, 0x07, 0x01,
                0x03, 0x04, 0x00, 0x0A, 0x00, 0x14
            };

            var (fn, data) = ModbusFrame.ParseResponse(frame, 1);

            AssertEq(0x03, fn, "功能码");
            AssertEq(5, data.Length, "数据段长度");

            var regs = ModbusFrame.ParseRegisters(data);

            AssertEq(2, regs.Length, "寄存器数");
            AssertEq(10, regs[0], "寄存器 0 应为 10");
            AssertEq(20, regs[1], "寄存器 1 应为 20");
        });

        run("解析异常响应:抛异常", () =>
        {
            // 功能码 0x83 = 03 异常,异常码 02 = 非法数据地址
            var frame = new byte[]
            {
                0x00, 0x01, 0x00, 0x00, 0x00, 0x03, 0x01,
                0x83, 0x02
            };

            try
            {
                ModbusFrame.ParseResponse(frame, 1);
            }
            catch (ModbusException ex)
            {
                if (!ex.Message.Contains("非法数据地址"))
                {
                    throw new Exception("异常消息应含中文说明,实际: " + ex.Message);
                }

                return;
            }

            throw new Exception("异常响应必须抛 ModbusException");
        });

        run("事务号不匹配被拒绝", () =>
        {
            var frame = new byte[]
            {
                0x00, 0x02, 0x00, 0x00, 0x00, 0x05, 0x01,
                0x03, 0x04, 0x00, 0x0A, 0x00, 0x14
            };

            try
            {
                ModbusFrame.ParseResponse(frame, 1);
            }
            catch (ModbusException)
            {
                return;
            }

            throw new Exception("事务号不匹配必须抛异常");
        });

        run("长度字段不匹配被拒绝", () =>
        {
            var frame = new byte[]
            {
                0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x01,
                0x03, 0x04, 0x00, 0x0A
            };

            try
            {
                ModbusFrame.ParseResponse(frame, 1);
            }
            catch (ModbusException)
            {
                return;
            }

            throw new Exception("长度不匹配必须抛异常");
        });

        run("解析线圈响应:位序正确", () =>
        {
            // 字节 0b00000101 = 线圈 0 和 2 为 ON
            var data = new byte[] { 0x01, 0x05 };

            var coils = ModbusFrame.ParseCoils(data, 8);

            AssertEq(8, coils.Length, "线圈数");
            AssertTrue(coils[0], "线圈 0 应 ON");
            AssertTrue(!coils[1], "线圈 1 应 OFF");
            AssertTrue(coils[2], "线圈 2 应 ON");
            AssertTrue(!coils[3], "线圈 3 应 OFF");
        });

        run("写响应回显校验:地址", () =>
        {
            var data = new byte[] { 0x00, 0x10, 0xFF, 0x00 };

            ModbusFrame.VerifyWriteEcho(0x05, data, 0x0010, 0xFF00);
        });

        run("写响应回显校验:地址不符被拒绝", () =>
        {
            var data = new byte[] { 0x00, 0x11, 0xFF, 0x00 };

            try
            {
                ModbusFrame.VerifyWriteEcho(0x05, data, 0x0010, 0xFF00);
            }
            catch (ModbusException)
            {
                return;
            }

            throw new Exception("地址不符必须抛异常");
        });

        run("写响应回显校验:值不符被拒绝", () =>
        {
            // 请求 ON(0xFF00) 但设备回显 OFF(0x0000)
            var data = new byte[] { 0x00, 0x10, 0x00, 0x00 };

            try
            {
                ModbusFrame.VerifyWriteEcho(0x05, data, 0x0010, 0xFF00);
            }
            catch (ModbusException ex)
            {
                if (!ex.Message.Contains("值不匹配"))
                {
                    throw new Exception("应报告值不匹配,实际: " + ex.Message);
                }

                return;
            }

            throw new Exception("值不符必须抛异常");
        });

        run("线圈写往返:ON 编码再解码一致", () =>
        {
            var pdu = ModbusFrame.BuildWriteSingleCoil(5, true);

            // 模拟设备回显
            var echo = new byte[] { pdu[1], pdu[2], pdu[3], pdu[4] };

            ModbusFrame.VerifyWriteEcho(ModbusFunction.WriteSingleCoil, echo, 5, ModbusFrame.CoilOn);
        });

        run("异常码说明表", () =>
        {
            AssertStr("非法数据地址", ModbusFrame.DescribeExceptionCode(2), "码 2");
            AssertStr("从站设备故障", ModbusFrame.DescribeExceptionCode(4), "码 4");
            AssertStr("未知异常码", ModbusFrame.DescribeExceptionCode(99), "码 99");
        });
    }

}