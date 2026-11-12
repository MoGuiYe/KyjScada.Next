namespace KyjScada.Infrastructure.Modbus;

/// <summary>
/// Modbus RTU 帧的封装/拆解(**纯函数,不碰 IO,可单测**)。
///
/// RTU 帧 = 从站号(1) + PDU(n) + CRC16(2,低字节先)。
/// 与 Modbus TCP 的区别:没有 MBAP 头(事务号/长度/单元号那 7 个字节),
/// 所以**不能**拿 Modbus TCP 的客户端去发 —— 网关会直接拒收。
/// </summary>
public static class ModbusRtuFrame
{
    /// <summary>把一个 PDU 包成完整 RTU 帧(含 CRC)。</summary>
    public static byte[] Wrap(byte unitId, byte[] pdu)
    {
        var frame = new byte[pdu.Length + 3];

        frame[0] = unitId;
        Array.Copy(pdu, 0, frame, 1, pdu.Length);

        var crc = ModbusCrc16.Compute(frame, 0, pdu.Length + 1);

        // Modbus 规定低字节先发
        frame[^2] = (byte)(crc & 0xFF);
        frame[^1] = (byte)(crc >> 8);

        return frame;
    }

    /// <summary>校验帧的 CRC 是否正确。</summary>
    public static bool VerifyCrc(byte[] frame)
    {
        if (frame is null || frame.Length < 4) return false;

        var crc = ModbusCrc16.Compute(frame, 0, frame.Length - 2);

        var expectLo = (byte)(crc & 0xFF);
        var expectHi = (byte)(crc >> 8);

        return frame[^2] == expectLo && frame[^1] == expectHi;
    }

    /// <summary>取出 PDU 部分(去掉从站号与 CRC)。</summary>
    public static byte[] ExtractPdu(byte[] frame)
    {
        if (frame is null || frame.Length < 4)
        {
            throw new ModbusException("RTU 帧过短: " + (frame?.Length ?? 0));
        }

        var pdu = new byte[frame.Length - 3];

        Array.Copy(frame, 1, pdu, 0, pdu.Length);

        return pdu;
    }

    /// <summary>
    /// 根据响应开头判断整帧长度。
    ///
    /// 串口没法像 TCP 那样靠"长度字段"知道还要读多少,必须按功能码推算:
    ///  - 异常响应(功能码最高位置 1):从站号+功能码+异常码+CRC = 5
    ///  - 读类(01~04):从站号+功能码+字节数+数据+CRC,长度要等字节数到了才知道
    ///  - 写单个(05/06)、写多个(0F/10):固定 8 字节(回显 4 字节数据)
    /// </summary>
    public static int ResponseLength(byte function, byte byteCountField)
    {
        if ((function & 0x80) != 0) return 5;

        switch (function)
        {
            case ModbusFunction.ReadCoils:
            case ModbusFunction.ReadDiscreteInputs:
            case ModbusFunction.ReadHoldingRegisters:
            case ModbusFunction.ReadInputRegisters:
                return 3 + byteCountField + 2;

            case ModbusFunction.WriteSingleCoil:
            case ModbusFunction.WriteSingleRegister:
            case 0x0F:  // Write Multiple Coils
            case 0x10:  // Write Multiple Registers
                return 8;

            default:
                throw new ModbusException("无法推算响应长度,功能码 0x" + function.ToString("X2"));
        }
    }

    /// <summary>
    /// 按功能码分段读出一整帧 RTU 响应。
    ///
    /// **与传输介质无关**:串口和 "RTU over TCP" 的帧格式完全一样,
    /// 差别只在"怎么读到 N 个字节"。所以这里只依赖一个 readExact 委托,
    /// 串口传 SerialPort 的读、TCP 传 NetworkStream 的读 —— 逻辑一份,两边不会跑偏。
    ///
    /// 注意必须精确跟踪已读字节:读类响应比写响应多一个"字节数"字段,
    /// 若续读长度统一按"总长-2"算就会多要 1 字节,结果是永远等不到 → 必然超时
    /// (这是本项目真实踩过的 bug)。
    /// </summary>
    public static byte[] ReadResponse (Func<int, byte[]> readExact)
    {
        // 从站号 + 功能码
        var head = readExact (2);

        var function = head[1];

        // 异常应答:从站号+功能码|0x80+异常码+CRC(2) = 5
        if ((function & 0x80) != 0)
        {
            var rest = readExact (3);

            var frame = new byte[5];
            frame[0] = head[0];
            frame[1] = head[1];
            Array.Copy (rest, 0, frame, 2, 3);

            return frame;
        }

        // 读应答:从站号+功能码+字节数+N+CRC(2)
        if (function is ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs
            or ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters)
        {
            var byteCount = readExact (1)[0];

            var total = 3 + byteCount + 2;

            // 这里以前写成 `byteCount + 2 == 0 ? Array.Empty<byte>() : readExact(...)`。
            // byteCount 是 byte(最小 0),byteCount + 2 永远 >= 2,那个分支是死代码 ——
            // 留着会让人误以为存在"不需要读 CRC"的路径。CRC 那 2 个字节**任何情况都要读**。
            var rest = readExact (byteCount + 2);

            var frame = new byte[total];
            frame[0] = head[0];
            frame[1] = head[1];
            frame[2] = byteCount;
            Array.Copy (rest, 0, frame, 3, rest.Length);

            return frame;
        }

        // 写应答(回显):从站号+功能码+地址(2)+值/数量(2)+CRC(2) = 8
        var echo = readExact (6);

        var echoFrame = new byte[8];
        echoFrame[0] = head[0];
        echoFrame[1] = head[1];
        Array.Copy (echo, 0, echoFrame, 2, 6);

        return echoFrame;
    }
}