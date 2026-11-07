using KyjScada.Domain.Config;

namespace KyjScada.Infrastructure.Modbus;

/// <summary>
/// 把工程值编码成寄存器字(写入方向)。**必须与读取方向的解码严格互逆**,
/// 否则写下去的值读回来会变样 —— 现场表现为"设了 50,回显 8.5E-41"。
///
/// 解码方向(ToBytes)做的事:
///   寄存器(线上顺序) → [字交换] → [字节交换] → 展开成大端字节 → [小端机反转] → BitConverter
/// 所以编码方向就是反过来:
///   值 → 大端字节 → [按同样规则反做字/字节交换] → 打包成寄存器
/// 交换操作是自逆的(交换两次回到原样),因此两处用的是同一组操作。
/// </summary>
public static class ModbusValueCodec
{
    /// <summary>该类型是否支持写入,以及占几个寄存器(0 = 不支持)。</summary>
    public static int RegisterCountForWrite(string? varType, out string reason)
    {
        var t = (varType ?? "").Trim().ToLowerInvariant();

        switch (t)
        {
            case "bool": case "boolean":
                reason = "";
                return 0;   // Bool 走线圈,不占寄存器

            case "byte": case "sbyte": case "short": case "ushort": case "word":
                reason = "";
                return 1;

            case "int": case "int32": case "uint": case "uint32": case "dword":
            case "float": case "single":
                reason = "";
                return 2;

            case "double": case "long": case "int64": case "ulong": case "uint64":
                reason = "64 位类型暂不支持写入(需要 4 寄存器写,尚未实现)";
                return -1;

            case "string": case "bytearray":
                reason = "字符串/字节数组类型不支持写入";
                return -1;

            default:
                reason = "未知类型 " + varType;
                return -1;
        }
    }

    /// <summary>
    /// 编码成寄存器字。返回 null 表示该类型/值不可编码。
    /// </summary>
    public static ushort[]? Encode(double value, VariableConfig v, WordOrder order)
    {
        var t = (v.VarType ?? "").Trim().ToLowerInvariant();

        try
        {
            switch (t)
            {
                case "byte":
                case "sbyte":
                {
                    // 8 位值只占一个字节,必须与解码(ConvertRawToValue)位置一致:
                    // BADC/DCBA 放低字节,ABCD/CDAB 放高字节。
                    // 以前这两种类型走的是 16 位分支,写 5 读回 0。
                    var b = t == "sbyte" ? (byte)(sbyte)Math.Round(value) : (byte)Math.Round(value);

                    var reg = order == WordOrder.Badc || order == WordOrder.Dcba
                        ? (ushort)b
                        : (ushort)(b << 8);

                    return new[] { reg };
                }

                case "short":
                case "ushort":
                case "word":
                {
                    var raw = (ushort)(short)Math.Round(value);

                    // BADC/DCBA 下单个寄存器内的字节也是反的(老系统 DataFormat 约定,
                    // 见 KYJScadaProWpf/DataConvertLib/ByteArrayLib.Get2Bytes:ABCD 反转、BADC 原样)。
                    // 读取侧 ConvertRawToValue 做同样的交换 —— 两侧必须严格对称,否则写 5 读回 0。
                    return new[]
                    {
                        order == WordOrder.Badc || order == WordOrder.Dcba
                            ? (ushort)((raw >> 8) | (raw << 8))
                            : raw
                    };
                }

                case "int":
                case "int32":
                case "uint":
                case "uint32":
                case "dword":
                {
                    var bits = t == "uint" || t == "uint32" || t == "dword"
                        ? (uint)Math.Round(value)
                        : (uint)(int)Math.Round(value);

                    var big = new[]
                    {
                        (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits
                    };

                    return PackWords(big, order);
                }

                case "float":
                case "single":
                {
                    var f = (float)value;

                    var le = BitConverter.GetBytes(f);

                    // 转成大端字节序 [A B C D]
                    var big = new byte[4];
                    big[0] = le[3];
                    big[1] = le[2];
                    big[2] = le[1];
                    big[3] = le[0];

                    return PackWords(big, order);
                }

                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把大端字节数组按字节序规则打包成寄存器。
    /// 字节序规则与读取方向完全对称:先做字节交换,再做字交换。
    /// </summary>
    private static ushort[] PackWords(byte[] bigEndian, WordOrder order)
    {
        var b = (byte[])bigEndian.Clone();

        var swapBytes = order == WordOrder.Badc || order == WordOrder.Dcba;
        var swapWords = order == WordOrder.Cdab || order == WordOrder.Dcba;

        if (swapBytes)
        {
            // 每 2 字节一组内部交换(A B → B A)
            for (int i = 0; i + 1 < b.Length; i += 2)
            {
                (b[i], b[i + 1]) = (b[i + 1], b[i]);
            }
        }

        if (swapWords)
        {
            // 交换两个 2 字节组(AB CD → CD AB)
            var half = b.Length / 2;

            for (int i = 0; i < half; i++)
            {
                (b[i], b[i + half]) = (b[i + half], b[i]);
            }
        }

        var regs = new ushort[b.Length / 2];

        for (int i = 0; i < regs.Length; i++)
        {
            regs[i] = (ushort)((b[i * 2] << 8) | b[i * 2 + 1]);
        }

        return regs;
    }
}