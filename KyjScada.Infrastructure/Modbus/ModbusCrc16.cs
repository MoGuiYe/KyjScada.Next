namespace KyjScada.Infrastructure.Modbus;

/// <summary>
/// Modbus RTU 的 CRC16 校验。
///
/// 标准算法:初值 0xFFFF,多项式 0xA001(反向表示),**低字节先发**。
/// RTU 帧没有 MBAP 头,靠这个 CRC 判完整性 —— 算错就会把所有正常帧判成坏帧,
/// 现场表现是"每帧都超时/校验错",极易误判成接线问题。
/// </summary>
public static class ModbusCrc16
{
    /// <summary>计算 CRC16,返回 16 位寄存器值。</summary>
    public static ushort Compute(byte[] data, int offset, int count)
    {
        if (data is null) return 0xFFFF;

        var crc = (ushort)0xFFFF;

        for (int i = 0; i < count; i++)
        {
            crc ^= data[offset + i];

            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 0x0001) != 0)
                {
                    crc = (ushort)((crc >> 1) ^ 0xA001);
                }
                else
                {
                    crc = (ushort)(crc >> 1);
                }
            }
        }

        return crc;
    }

    public static ushort Compute(byte[] data)
    {
        return data is null ? (ushort)0xFFFF : Compute(data, 0, data.Length);
    }
}