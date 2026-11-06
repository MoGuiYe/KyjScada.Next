using KyjScada.Domain.Config;
using KyjScada.Domain.Protocol;

namespace KyjScada.Domain.Tests;

public static class ProtocolTests
{
    public static void RunAll(TestRunner t)
    {
        t.Run("协议注册表含 11 种协议", () =>
        {
            Assert.Equal(11, ProtocolRegistry.All().Count, "协议数");
        });

        t.Run("ModbusTCP 地址:4 区短地址", () =>
        {
            var dev = new DeviceConfig { ProtocolKey = "ModbusTCP" };
            dev.Options["IsShortAddress"] = "True";

            var addr = ProtocolRegistry.ResolveAddress("ModbusTCP", "1.400001", "0", dev);
            Assert.Equal("1.40001", addr, "400001+0 短地址应为 40001");
        });

        t.Run("ModbusTCP 地址:4 区长地址", () =>
        {
            var dev = new DeviceConfig { ProtocolKey = "ModbusTCP" };
            dev.Options["IsShortAddress"] = "False";

            var addr = ProtocolRegistry.ResolveAddress("ModbusTCP", "1.400001", "0", dev);
            Assert.Equal("1.400001", addr, "长地址应保持 400001");
        });

        t.Run("ModbusTCP 地址:偏移累加", () =>
        {
            var dev = new DeviceConfig { ProtocolKey = "ModbusTCP" };
            dev.Options["IsShortAddress"] = "True";

            var addr = ProtocolRegistry.ResolveAddress("ModbusTCP", "1.400001", "10", dev);
            Assert.Equal("1.40011", addr, "40001+10 应为 40011");
        });

        t.Run("ModbusTCP 地址:3 区输入寄存器", () =>
        {
            var dev = new DeviceConfig { ProtocolKey = "ModbusTCP" };
            dev.Options["IsShortAddress"] = "True";

            var addr = ProtocolRegistry.ResolveAddress("ModbusTCP", "1.300001", "5", dev);
            Assert.Equal("1.30006", addr, "30001+5 应为 30006");
        });

        t.Run("ModbusTCP 地址:0 区线圈", () =>
        {
            var dev = new DeviceConfig { ProtocolKey = "ModbusTCP" };
            dev.Options["IsShortAddress"] = "True";

            var addr = ProtocolRegistry.ResolveAddress("ModbusTCP", "1.000001", "3", dev);
            Assert.Equal("1.4", addr, "线圈 1+3 应为 4");
        });

        t.Run("ModbusTCP 地址:小数偏移被拒绝", () =>
        {
            var dev = new DeviceConfig { ProtocolKey = "ModbusTCP" };
            dev.Options["IsShortAddress"] = "True";

            // 老代码用 float.TryParse,"10.5" 会生成非法地址 "1.40011.5"
            var addr = ProtocolRegistry.ResolveAddress("ModbusTCP", "1.400001", "10.5", dev);
            Assert.Equal(string.Empty, addr, "小数偏移必须返回空,不能生成非法地址");
        });

        t.Run("ModbusTCP 地址:非法组起始返回空", () =>
        {
            var dev = new DeviceConfig { ProtocolKey = "ModbusTCP" };

            Assert.Equal(string.Empty, ProtocolRegistry.ResolveAddress("ModbusTCP", "400001", "0", dev), "缺站号");
            Assert.Equal(string.Empty, ProtocolRegistry.ResolveAddress("ModbusTCP", "1.", "0", dev), "区标识为空");
            Assert.Equal(string.Empty, ProtocolRegistry.ResolveAddress("ModbusTCP", "1.9", "0", dev), "未知区标识 9");
        });

        t.Run("西门子 DB 区地址", () =>
        {
            var dev = new DeviceConfig { ProtocolKey = "Siemens" };

            var addr = ProtocolRegistry.ResolveAddress("Siemens", "DB1.0", "10", dev);
            Assert.Equal("DB1.10", addr, "DB1.0 + 10 应为 DB1.10");
        });

        t.Run("西门子非 DB 区:组基准参与计算", () =>
        {
            var dev = new DeviceConfig { ProtocolKey = "Siemens" };

            // 老代码这里会生成 "M.10"(丢掉基准 100)
            var addr = ProtocolRegistry.ResolveAddress("Siemens", "M100", "10", dev);
            Assert.Equal("M.110", addr, "M100 + 10 应为 M.110,不能丢掉基准");
        });

        t.Run("三菱:组基准参与计算", () =>
        {
            var dev = new DeviceConfig { ProtocolKey = "Melsec" };

            // 老代码这里会生成 "D10"(丢掉基准 100)
            var addr = ProtocolRegistry.ResolveAddress("Melsec", "D100", "10", dev);
            Assert.Equal("D110", addr, "D100 + 10 应为 D110");
        });

        t.Run("三菱:基准为 0 时也正确", () =>
        {
            var dev = new DeviceConfig { ProtocolKey = "Melsec" };

            var addr = ProtocolRegistry.ResolveAddress("Melsec", "D0", "10", dev);
            Assert.Equal("D10", addr, "D0 + 10 应为 D10");
        });

        t.Run("欧姆龙:组基准参与计算", () =>
        {
            var dev = new DeviceConfig { ProtocolKey = "Omron" };

            var addr = ProtocolRegistry.ResolveAddress("Omron", "D100", "10", dev);
            Assert.Equal("D110", addr, "D100 + 10 应为 D110");
        });

        t.Run("地址校验:Modbus 超范围", () =>
        {
            var dev = new DeviceConfig { ProtocolKey = "ModbusTCP" };
            var d = ProtocolRegistry.Find("ModbusTCP")!;

            Assert.True(d.ValidateAddress("1.70000", dev) is not null, "70000 应被判超范围");
            Assert.True(d.ValidateAddress("1.40001", dev) is null, "40001 应合法");
            Assert.True(d.ValidateAddress("300.40001", dev) is not null, "站号 300 应被判非法");
        });

        t.Run("协议参数:ModbusTCP 声明 IsShortAddress", () =>
        {
            var d = ProtocolRegistry.Find("ModbusTCP")!;

            Assert.Equal(1, d.Options.Count, "ModbusTCP 应有 1 个参数");
            Assert.Equal("IsShortAddress", d.Options[0].Key, "参数键");
            Assert.Equal(OptionType.Bool, d.Options[0].Type, "参数类型");
        });

        t.Run("协议参数:OPCUA 必填 ServerUrl", () =>
        {
            var d = ProtocolRegistry.Find("OPCUA")!;
            var dev = new DeviceConfig { ProtocolKey = "OPCUA" };

            Assert.True(d.ValidateOptions(dev) is not null, "缺 ServerUrl 应报错");

            dev.Options["ServerUrl"] = "opc.tcp://localhost:4840";
            Assert.True(d.ValidateOptions(dev) is null, "填了 ServerUrl 应通过");
        });

        t.Run("未知协议返回空地址", () =>
        {
            var dev = new DeviceConfig();
            Assert.Equal(string.Empty, ProtocolRegistry.ResolveAddress("NotExist", "D100", "10", dev), "未知协议");
        });

        t.Run("连接方式:串口协议标记为 Serial", () =>
        {
            Assert.Equal(ConnectionKind.Serial, ProtocolRegistry.Find("ModbusRTU")!.ConnectionKind, "RTU 是串口");
            Assert.Equal(ConnectionKind.Serial, ProtocolRegistry.Find("ModbusASCII")!.ConnectionKind, "ASCII 是串口");
            Assert.Equal(ConnectionKind.Serial, ProtocolRegistry.Find("MelsecFXSerial")!.ConnectionKind, "FX 是串口");
            Assert.Equal(ConnectionKind.Serial, ProtocolRegistry.Find("OmronHostlink")!.ConnectionKind, "Hostlink 是串口");
        });

        t.Run("连接方式:TCP 协议标记为 Tcp", () =>
        {
            Assert.Equal(ConnectionKind.Tcp, ProtocolRegistry.Find("ModbusTCP")!.ConnectionKind, "TCP");
            Assert.Equal(ConnectionKind.Tcp, ProtocolRegistry.Find("ModbusRTUOverTCP")!.ConnectionKind, "RTUOverTCP 走网络");
            Assert.Equal(ConnectionKind.Tcp, ProtocolRegistry.Find("Siemens")!.ConnectionKind, "西门子");
            Assert.Equal(ConnectionKind.Tcp, ProtocolRegistry.Find("Melsec")!.ConnectionKind, "三菱");
            Assert.Equal(ConnectionKind.Tcp, ProtocolRegistry.Find("Omron")!.ConnectionKind, "欧姆龙");
        });
    }
}
