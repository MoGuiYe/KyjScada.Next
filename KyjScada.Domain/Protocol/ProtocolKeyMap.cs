namespace KyjScada.Domain.Protocol;

/// <summary>
/// 协议键与设备地址族的映射。采集器据此决定用哪个功能码读。
///
/// 老系统把「读什么区」硬编码在 11 个设备子类里,加协议要改 6 处。
/// 这里集中成一张表,新增协议只改一处。
/// </summary>
public static class ProtocolKeyMap
{
    /// <summary>Modbus 家族:用功能码读。</summary>
    public static bool IsModbusFamily (string protocolKey)
    {
        if (protocolKey is null) return false;

        return protocolKey == "ModbusTCP"
            || protocolKey == "ModbusRTU"
            || protocolKey == "ModbusRTUOverTCP"
            || protocolKey == "ModbusASCII";
    }

    /// <summary>
    /// 西门子家族:地址形如 DB1.10 / M.110。
    /// 描述符当前注册的键是 "Siemens";后面加 S7-1200/1500 等细分键时一并认。
    /// </summary>
    public static bool IsSiemensFamily (string protocolKey)
    {
        if (protocolKey is null) return false;

        return protocolKey == "Siemens"
            || protocolKey == "SiemensS7_1200"
            || protocolKey == "SiemensS7_1500"
            || protocolKey == "SiemensS7_200Smart"
            || protocolKey == "SiemensS7_300";
    }

    public static bool IsMelsecFamily (string protocolKey)
    {
        if (protocolKey is null) return false;

        return protocolKey == "Melsec"
            || protocolKey == "MelsecFXSerial"
            || protocolKey == "MelsecMcBinary"
            || protocolKey == "MelsecMcAscii";
    }

    public static bool IsOmronFamily (string protocolKey)
    {
        if (protocolKey is null) return false;

        return protocolKey == "Omron"
            || protocolKey == "OmronHostlink"
            || protocolKey == "OmronFinsTcp";
    }

    public static bool IsOpcFamily (string protocolKey)
    {
        if (protocolKey is null) return false;

        return protocolKey == "OPCDA" || protocolKey == "OPCUA";
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
        return protocolKey == "ModbusTCP"
            || protocolKey == "ModbusRTU"
            || protocolKey == "ModbusRTUOverTCP";
    }
}
