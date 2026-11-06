using System.Globalization;
using KyjScada.Domain.Config;

namespace KyjScada.Domain.Protocol;

/// <summary>
/// 地址合成公共逻辑。
///
/// 老项目这里踩过一个坑:原实现直接取 gpStart 首字符再拼 varStart,
/// 丢掉组起始的数值部分 —— 组起始 "D100" + 偏移 10 会导出成 "D10" 而非 "D110",
/// 只有组基准恒为 0 时才碰巧正确。
/// 这里改为「区域前缀 + 组基准 + 偏移」相加。
/// </summary>
internal static class AddressCombiner
{
    /// <summary>
    /// 合成「前缀 + 数字」型地址。
    /// </summary>
    /// <param name="groupStart">组起始,如 "D100" / "M0" / "DB1.0"</param>
    /// <param name="varOffset">组内偏移,如 "10"</param>
    /// <param name="withDot">true 时生成 "M.110"(西门子非 DB 区);false 时生成 "D110"</param>
    public static string Combine(string groupStart, string varOffset, bool withDot)
    {
        if (string.IsNullOrWhiteSpace(groupStart) || string.IsNullOrWhiteSpace(varOffset))
        {
            return string.Empty;
        }

        var area = groupStart.Substring(0, 1);
        var baseText = groupStart.Substring(1).Trim();

        if (int.TryParse(baseText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baseValue)
            && int.TryParse(varOffset.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
        {
            var number = (baseValue + offset).ToString(CultureInfo.InvariantCulture);
            return withDot ? area + "." + number : area + number;
        }

        // 脏数据兜底:保持旧行为,不因地址不可解析而中断整次导出
        return withDot ? area + "." + varOffset : area + varOffset;
    }
}

/// <summary>
/// 西门子 S7。
///
/// 两种地址形态:
///  - DB 区:"DB1.0" + 偏移 10 -> "DB1.10"
///  - 非 DB 区:"M0" + 偏移 110 -> "M.110"
/// </summary>
public sealed class SiemensDescriptor : IProtocolDescriptor
{
    public string Key => "Siemens";

    public string DisplayName => "西门子 S7";

    public ConnectionKind ConnectionKind => ConnectionKind.Tcp;

    public IReadOnlyList<OptionDefinition> Options => new[]
    {
        new OptionDefinition("CPUType", "CPU 类型", OptionType.Enum, "S1200",
            new[] { "S200", "S300", "S400", "S1200", "S1500" }),
        new OptionDefinition("Rack", "机架号", OptionType.Int, "0"),
        new OptionDefinition("Slot", "槽号", OptionType.Int, "1")
    };

    public string BuildAddress(string groupStart, string varOffset, DeviceConfig device)
    {
        if (string.IsNullOrWhiteSpace(groupStart) || string.IsNullOrWhiteSpace(varOffset))
        {
            return string.Empty;
        }

        // DB 区:保留 DB 号,只替换点号后的偏移
        if (groupStart.Length >= 2 && groupStart.Substring(0, 2).ToUpperInvariant() == "DB")
        {
            if (!groupStart.Contains('.'))
            {
                return string.Empty;
            }

            var dbPart = groupStart.Substring(0, groupStart.IndexOf('.'));
            return dbPart + "." + varOffset.Trim();
        }

        // 非 DB 区:区域前缀 + 基准 + 偏移
        return AddressCombiner.Combine(groupStart, varOffset, true);
    }

    public string? ValidateAddress(string fullAddress, DeviceConfig device)
    {
        if (string.IsNullOrWhiteSpace(fullAddress))
        {
            return "地址为空";
        }

        // DB1.10 或 M.110 两种形态
        if (fullAddress.StartsWith("DB", StringComparison.OrdinalIgnoreCase))
        {
            var parts = fullAddress.Split('.');
            if (parts.Length != 2 || !int.TryParse(parts[1], out _))
            {
                return "DB 区地址格式应为 DB号.偏移,如 DB1.10";
            }
            return null;
        }

        var area = fullAddress[0];
        if (!"MIQECADT".Contains(area))
        {
            return "未知存储区标识 " + area + "(应为 M/I/Q/E/C/A/D/T 之一)";
        }

        return null;
    }

    public string? ValidateOptions(DeviceConfig device)
    {
        return null;
    }
}

/// <summary>
/// 三菱 MC 协议。地址形如 "D100" + 偏移 10 -> "D110"。
/// </summary>
public sealed class MelsecDescriptor : IProtocolDescriptor
{
    public string Key => "Melsec";

    public string DisplayName => "三菱 MC";

    public ConnectionKind ConnectionKind => ConnectionKind.Tcp;

    public IReadOnlyList<OptionDefinition> Options => new[]
    {
        new OptionDefinition("MelsecType", "系列", OptionType.Enum, "Binary",
            new[] { "Binary", "ASCII" })
    };

    public string BuildAddress(string groupStart, string varOffset, DeviceConfig device)
    {
        return AddressCombiner.Combine(groupStart, varOffset, false);
    }

    public string? ValidateAddress(string fullAddress, DeviceConfig device)
    {
        if (string.IsNullOrWhiteSpace(fullAddress))
        {
            return "地址为空";
        }

        if (!char.IsLetter(fullAddress[0]))
        {
            return "三菱地址应以区域字母开头,如 D100";
        }

        return null;
    }

    public string? ValidateOptions(DeviceConfig device)
    {
        return null;
    }
}

/// <summary>
/// 三菱 FX 串口。地址规则同 MC,只是走串口。
/// </summary>
public sealed class MelsecFXSerialDescriptor : IProtocolDescriptor
{
    private static readonly MelsecDescriptor Logic = new();

    public string Key => "MelsecFXSerial";

    public string DisplayName => "三菱 FX 串口";

    public ConnectionKind ConnectionKind => ConnectionKind.Serial;

    public IReadOnlyList<OptionDefinition> Options => Array.Empty<OptionDefinition>();

    public string BuildAddress(string groupStart, string varOffset, DeviceConfig device)
    {
        return Logic.BuildAddress(groupStart, varOffset, device);
    }

    public string? ValidateAddress(string fullAddress, DeviceConfig device)
    {
        return Logic.ValidateAddress(fullAddress, device);
    }

    public string? ValidateOptions(DeviceConfig device)
    {
        return null;
    }
}

/// <summary>
/// 欧姆龙 FinsTCP。地址形如 "D100" + 偏移 10 -> "D110"。
/// </summary>
public sealed class OmronDescriptor : IProtocolDescriptor
{
    public string Key => "Omron";

    public string DisplayName => "欧姆龙 Fins";

    public ConnectionKind ConnectionKind => ConnectionKind.Tcp;

    public IReadOnlyList<OptionDefinition> Options => Array.Empty<OptionDefinition>();

    public string BuildAddress(string groupStart, string varOffset, DeviceConfig device)
    {
        return AddressCombiner.Combine(groupStart, varOffset, false);
    }

    public string? ValidateAddress(string fullAddress, DeviceConfig device)
    {
        if (string.IsNullOrWhiteSpace(fullAddress))
        {
            return "地址为空";
        }

        if (!char.IsLetter(fullAddress[0])
            && !char.IsDigit(fullAddress[0]))
        {
            return "欧姆龙地址格式非法";
        }

        return null;
    }

    public string? ValidateOptions(DeviceConfig device)
    {
        return null;
    }
}

/// <summary>
/// 欧姆龙 Hostlink(串口)。多三个参数。
/// </summary>
public sealed class OmronHostlinkDescriptor : IProtocolDescriptor
{
    private static readonly OmronDescriptor Logic = new();

    public string Key => "OmronHostlink";

    public string DisplayName => "欧姆龙 Hostlink";

    public ConnectionKind ConnectionKind => ConnectionKind.Serial;

    public IReadOnlyList<OptionDefinition> Options => new[]
    {
        new OptionDefinition("UnitNo", "单元号", OptionType.Int, "0"),
        new OptionDefinition("SA2", "SA2 参数", OptionType.Int, "0"),
        new OptionDefinition("ResponseWaitTime", "响应等待时间", OptionType.Int, "0")
    };

    public string BuildAddress(string groupStart, string varOffset, DeviceConfig device)
    {
        return Logic.BuildAddress(groupStart, varOffset, device);
    }

    public string? ValidateAddress(string fullAddress, DeviceConfig device)
    {
        return Logic.ValidateAddress(fullAddress, device);
    }

    public string? ValidateOptions(DeviceConfig device)
    {
        return null;
    }
}
