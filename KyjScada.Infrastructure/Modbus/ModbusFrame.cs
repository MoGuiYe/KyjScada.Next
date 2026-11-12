namespace KyjScada.Infrastructure.Modbus;

/// <summary>Modbus 功能码。</summary>
public static class ModbusFunction
{
    public const byte ReadCoils = 0x01;
    public const byte ReadDiscreteInputs = 0x02;
    public const byte ReadHoldingRegisters = 0x03;
    public const byte ReadInputRegisters = 0x04;
    public const byte WriteSingleCoil = 0x05;
    public const byte WriteSingleRegister = 0x06;
    public const byte WriteMultipleCoils = 0x0F;
    public const byte WriteMultipleRegisters = 0x10;
}

/// <summary>
/// Modbus 帧编解码。**纯函数,不碰网络**,便于单元测试。
///
/// 老系统的教训:ModbusTCP 没实现 WriteBool,基类直接返回失败,
/// 导致现场点动一直写不进去且不报错。所以这里把线圈写单列出来,
/// 并且有专门的往返测试。
///
/// 线圈值编码规则(Modbus 规范):
///   true  = 0xFF00
///   false = 0x0000
/// 这两个值不是 1/0,写错会被设备拒绝或误解释。
/// </summary>
public static class ModbusFrame
{
    public const ushort CoilOn = 0xFF00;
    public const ushort CoilOff = 0x0000;

    /// <summary>MBAP 头长度(TCP 专用)。事务2 + 协议2 + 长度2 + 单元1 = 7。</summary>
    public const int MbapHeaderLength = 7;

    /// <summary>构建 MBAP 头。</summary>
    /// <param name="transactionId">事务号,响应会原样带回</param>
    /// <param name="length">后续 PDU 字节数</param>
    /// <param name="unitId">从站地址</param>
    public static byte[] BuildMbap(ushort transactionId, ushort length, byte unitId)
    {
        var header = new byte[MbapHeaderLength];

        header[0] = (byte)(transactionId >> 8);
        header[1] = (byte)(transactionId & 0xFF);

        // 协议标识固定 0
        header[2] = 0;
        header[3] = 0;

        header[4] = (byte)(length >> 8);
        header[5] = (byte)(length & 0xFF);

        header[6] = unitId;

        return header;
    }

    /// <summary>构建「写单个线圈」PDU(功能码 05)。</summary>
    public static byte[] BuildWriteSingleCoil(ushort address, bool value)
    {
        var pdu = new byte[5];

        pdu[0] = ModbusFunction.WriteSingleCoil;
        pdu[1] = (byte)(address >> 8);
        pdu[2] = (byte)(address & 0xFF);

        var encoded = value ? CoilOn : CoilOff;

        pdu[3] = (byte)(encoded >> 8);
        pdu[4] = (byte)(encoded & 0xFF);

        return pdu;
    }

    /// <summary>构建「写单个寄存器」PDU(功能码 06)。</summary>
    public static byte[] BuildWriteSingleRegister(ushort address, ushort value)
    {
        var pdu = new byte[5];

        pdu[0] = ModbusFunction.WriteSingleRegister;
        pdu[1] = (byte)(address >> 8);
        pdu[2] = (byte)(address & 0xFF);
        pdu[3] = (byte)(value >> 8);
        pdu[4] = (byte)(value & 0xFF);

        return pdu;
    }

    /// <summary>
    /// 构建「写多个寄存器」PDU(功能码 16)。
    /// 用于写 Float/Int32 这类占 2 个寄存器的设定值 —— 功能码 06 只能写 1 个寄存器。
    /// </summary>
    public static byte[] BuildWriteMultipleRegisters(ushort startAddress, ushort[] values)
    {
        if (values is null || values.Length == 0)
        {
            throw new ModbusException("写寄存器数量不能为 0");
        }

        if (values.Length > 123)
        {
            throw new ModbusException("一次最多写 123 个寄存器,实际 " + values.Length);
        }

        var pdu = new byte[6 + values.Length * 2];

        pdu[0] = ModbusFunction.WriteMultipleRegisters;
        pdu[1] = (byte)(startAddress >> 8);
        pdu[2] = (byte)(startAddress & 0xFF);
        pdu[3] = (byte)(values.Length >> 8);
        pdu[4] = (byte)(values.Length & 0xFF);
        pdu[5] = (byte)(values.Length * 2);

        for (int i = 0; i < values.Length; i++)
        {
            pdu[6 + i * 2] = (byte)(values[i] >> 8);
            pdu[7 + i * 2] = (byte)(values[i] & 0xFF);
        }

        return pdu;
    }

    /// <summary>
    /// 校验「写多个寄存器」的回显:功能码 + 起始地址(2) + 数量(2)。
    /// 与单寄存器写的回显结构不同(那里第 4-5 字节是值),不能混用校验函数。
    /// </summary>
    public static void VerifyWriteMultipleEcho(byte function, byte[] data, ushort startAddress, ushort quantity)
    {
        if (data is null || data.Length < 4)
        {
            throw new ModbusException("写多寄存器回显过短");
        }

        var echoStart = (ushort)((data[0] << 8) | data[1]);
        var echoQty = (ushort)((data[2] << 8) | data[3]);

        if (echoStart != startAddress)
        {
            throw new ModbusException(
                "回显起始地址不匹配: 期望 " + startAddress + ",实际 " + echoStart);
        }

        if (echoQty != quantity)
        {
            throw new ModbusException(
                "回显数量不匹配: 期望 " + quantity + ",实际 " + echoQty);
        }
    }

    /// <summary>构建「读保持寄存器」PDU(功能码 03)。</summary>
    public static byte[] BuildReadHoldingRegisters(ushort startAddress, ushort quantity)
    {
        var pdu = new byte[5];

        pdu[0] = ModbusFunction.ReadHoldingRegisters;
        pdu[1] = (byte)(startAddress >> 8);
        pdu[2] = (byte)(startAddress & 0xFF);
        pdu[3] = (byte)(quantity >> 8);
        pdu[4] = (byte)(quantity & 0xFF);

        return pdu;
    }

    /// <summary>构建「读线圈」PDU(功能码 01)。</summary>
    public static byte[] BuildReadCoils(ushort startAddress, ushort quantity)
    {
        var pdu = new byte[5];

        pdu[0] = ModbusFunction.ReadCoils;
        pdu[1] = (byte)(startAddress >> 8);
        pdu[2] = (byte)(startAddress & 0xFF);
        pdu[3] = (byte)(quantity >> 8);
        pdu[4] = (byte)(quantity & 0xFF);

        return pdu;
    }

    /// <summary>
    /// 把 MBAP + PDU 拼成完整帧。
    /// </summary>
    public static byte[] BuildFrame(ushort transactionId, byte unitId, byte[] pdu)
    {
        var frame = new byte[MbapHeaderLength + pdu.Length];

        var header = BuildMbap(transactionId, (ushort)(pdu.Length + 1), unitId);

        Array.Copy(header, 0, frame, 0, MbapHeaderLength);
        Array.Copy(pdu, 0, frame, MbapHeaderLength, pdu.Length);

        return frame;
    }

    /// <summary>
    /// 解析响应帧。返回 (功能码, 数据字节)。
    /// 异常响应(功能码最高位为 1)会抛 ModbusException。
    /// </summary>
    public static (byte Function, byte[] Data) ParseResponse(
        byte[] frame, ushort expectedTransactionId, byte? expectedUnitId = null)
    {
        if (frame.Length < MbapHeaderLength + 1)
        {
            throw new ModbusException("响应帧过短: " + frame.Length + " 字节");
        }

        var transactionId = (ushort)((frame[0] << 8) | frame[1]);

        if (transactionId != expectedTransactionId)
        {
            throw new ModbusException(
                "事务号不匹配: 期望 " + expectedTransactionId + ", 实际 " + transactionId);
        }

        var protocolId = (ushort)((frame[2] << 8) | frame[3]);

        if (protocolId != 0)
        {
            throw new ModbusException("协议标识应为 0,实际 " + protocolId);
        }

        var declaredLength = (ushort)((frame[4] << 8) | frame[5]);

        if (declaredLength != frame.Length - 6)
        {
            throw new ModbusException(
                "长度字段不匹配: 头部声明 " + declaredLength + ", 实际 " + (frame.Length - 6));
        }

        var function = frame[MbapHeaderLength];

        // 单元号必须与请求一致。网关后面挂多个从站时,回帧的单元号是"这是哪个从站答的"
        // 的唯一标识 —— 不校验就会把 A 从站的数据当成 B 从站发布出去(数值看着正常,其实串了设备)。
        // 两个 RTU 传输层都校验站号,这里原先漏了。
        if (expectedUnitId is { } unit && frame[6] != unit)
        {
            throw new ModbusException(
                "单元号不匹配: 期望 " + unit + ", 实际 " + frame[6]);
        }

        // 异常响应:功能码最高位置 1
        if ((function & 0x80) != 0)
        {
            var originalFunction = (byte)(function & 0x7F);
            var errorCode = frame.Length > MbapHeaderLength + 1
                ? frame[MbapHeaderLength + 1]
                : (byte)0;

            throw new ModbusException(
                "设备返回异常: 功能码 0x" + originalFunction.ToString("X2")
                + ",异常码 " + errorCode + "(" + DescribeExceptionCode(errorCode) + ")");
        }

        var dataLength = frame.Length - MbapHeaderLength - 1;
        var data = new byte[dataLength];

        Array.Copy(frame, MbapHeaderLength + 1, data, 0, dataLength);

        return (function, data);
    }

    /// <summary>解析「读保持寄存器」响应的数据段为 ushort 数组。</summary>
    public static ushort[] ParseRegisters(byte[] data)
    {
        if (data.Length < 1)
        {
            throw new ModbusException("寄存器响应无数据");
        }

        var byteCount = data[0];

        if (byteCount != data.Length - 1)
        {
            throw new ModbusException(
                "字节计数不匹配: 声明 " + byteCount + ", 实际 " + (data.Length - 1));
        }

        if (byteCount % 2 != 0)
        {
            throw new ModbusException("寄存器字节数必须为偶数,实际 " + byteCount);
        }

        var count = byteCount / 2;
        var result = new ushort[count];

        for (int i = 0; i < count; i++)
        {
            var hi = data[1 + i * 2];
            var lo = data[2 + i * 2];

            result[i] = (ushort)((hi << 8) | lo);
        }

        return result;
    }

    /// <summary>解析「读线圈」响应的数据段为 bool 数组。</summary>
    public static bool[] ParseCoils(byte[] data, int expectedCount)
    {
        if (data.Length < 1)
        {
            throw new ModbusException("线圈响应无数据");
        }

        var byteCount = data[0];

        if (byteCount != data.Length - 1)
        {
            throw new ModbusException(
                "字节计数不匹配: 声明 " + byteCount + ", 实际 " + (data.Length - 1));
        }

        var neededBytes = (expectedCount + 7) / 8;

        if (byteCount < neededBytes)
        {
            throw new ModbusException(
                "线圈数据不足: 需要 " + neededBytes + " 字节,实际 " + byteCount);
        }

        var result = new bool[expectedCount];

        for (int i = 0; i < expectedCount; i++)
        {
            var byteIndex = i / 8;
            var bitIndex = i % 8;

            result[i] = (data[1 + byteIndex] & (1 << bitIndex)) != 0;
        }

        return result;
    }

    /// <summary>校验「写单个线圈/寄存器」响应是否回显了请求。</summary>
    public static void VerifyWriteEcho(byte function, byte[] data, ushort address, ushort expectedRawValue)
    {
        if (data.Length != 4)
        {
            throw new ModbusException("写响应数据长度应为 4,实际 " + data.Length);
        }

        var echoAddress = (ushort)((data[0] << 8) | data[1]);

        if (echoAddress != address)
        {
            throw new ModbusException(
                "写响应地址不匹配: 请求 " + address + ",回显 " + echoAddress);
        }

        var echoValue = (ushort)((data[2] << 8) | data[3]);

        if (echoValue != expectedRawValue)
        {
            throw new ModbusException(
                "写响应值不匹配: 请求 0x" + expectedRawValue.ToString("X4")
                + ",回显 0x" + echoValue.ToString("X4"));
        }
    }

    /// <summary>异常码中文说明。</summary>
    public static string DescribeExceptionCode(byte code) => code switch
    {
        1 => "非法功能码",
        2 => "非法数据地址",
        3 => "非法数据值",
        4 => "从站设备故障",
        5 => "确认(处理中)",
        6 => "从站设备忙",
        8 => "存储奇偶性差错",
        10 => "网关路径不可用",
        11 => "网关目标设备无响应",
        _ => "未知异常码"
    };
}

/// <summary>Modbus 通讯异常。</summary>
public sealed class ModbusException : Exception
{
    public ModbusException(string message) : base(message)
    {
    }

    public ModbusException(string message, Exception inner) : base(message, inner)
    {
    }
}
