namespace KyjScada.Domain.Protocol;

/// <summary>
/// 协议键与设备地址族的映射。采集器据此决定用哪个功能码读。
///
/// 老系统把「读什么区」硬编码在 11 个设备子类里,加协议要改 6 处。
/// 这里集中成一张表,新增协议只改一处。
/// </summary>
public static class ProtocolKeyMap
{
    /// <summary>
    /// 协议键比较一律**忽略大小写**。
    /// 组态文件是人手写的,现场出现过 "modbusTCP"、"MODBUSRTU" 这类写法;
    /// 按大小写敏感比较会判成"未知协议",采集器于是交给别的分支去处理,
    /// 表现是"配置全对但一个点都采不上"。
    /// </summary>
    private static bool Is (string? protocolKey, params string[] keys)
    {
        if (protocolKey is null) return false;

        foreach (var key in keys)
        {
            if (string.Equals (protocolKey, key, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Modbus 家族:用功能码读。</summary>
    public static bool IsModbusFamily (string protocolKey)
    {
        return Is (protocolKey, "ModbusTCP", "ModbusRTU", "ModbusRTUOverTCP", "ModbusASCII");
    }

    /// <summary>
    /// 西门子家族:地址形如 DB1.10 / M.110。
    /// 描述符当前注册的键是 "Siemens";后面加 S7-1200/1500 等细分键时一并认。
    /// </summary>
    public static bool IsSiemensFamily (string protocolKey)
    {
        return Is (protocolKey, "Siemens", "SiemensS7_1200", "SiemensS7_1500",
            "SiemensS7_200Smart", "SiemensS7_300");
    }

    public static bool IsMelsecFamily (string protocolKey)
    {
        return Is (protocolKey, "Melsec", "MelsecFXSerial", "MelsecMcBinary", "MelsecMcAscii");
    }

    public static bool IsOmronFamily (string protocolKey)
    {
        return Is (protocolKey, "Omron", "OmronHostlink", "OmronFinsTcp");
    }

    public static bool IsOpcFamily (string protocolKey)
    {
        return Is (protocolKey, "OPCDA", "OPCUA");
    }

    /// <summary>
    /// 目前已实现采集的协议。
    ///
    /// 已实现:**ModbusTCP**(MBAP 帧)、**ModbusRTU**(串口 + CRC16)、
    /// **ModbusRTUOverTCP**(RTU 帧跑裸 TCP 流,无 MBAP)。
    /// 未实现:ModbusASCII / 西门子 / 三菱 / 欧姆龙 / OPC。
    /// </summary>
    public static bool IsImplementedInV1 (string protocolKey)
    {
        return Is (protocolKey, "ModbusTCP", "ModbusRTU", "ModbusRTUOverTCP");
    }
}
