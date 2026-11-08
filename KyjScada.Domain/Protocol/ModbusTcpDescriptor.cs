using System.Globalization;
using KyjScada.Domain.Config;

namespace KyjScada.Domain.Protocol;

/// <summary>
/// Modbus TCP 描述符。
///
/// 地址规则来自老项目 ConfigManage.GetModbusAddress 实测,
/// 但修掉了它的两个 bug:
///  1. 老代码用 float.TryParse 解析偏移,"10.5" 会生成非法地址 "41001.5"。
///     这里改为整数解析,小数直接判为非法。
///  2. 老代码短地址判定靠 Comments 字段(中文备注会被误判为短地址,
///     导致 40001 <-> 400001 偏移 36 万)。这里改为显式 Options 参数。
/// </summary>
public sealed class ModbusTcpDescriptor : IProtocolDescriptor
{
    public string Key => "ModbusTCP";

    public string DisplayName => "Modbus TCP";

    public ConnectionKind ConnectionKind => ConnectionKind.Tcp;

    public IReadOnlyList<OptionDefinition> Options => new[]
    {
        new OptionDefinition(
            "IsShortAddress",
            "短地址模式",
            OptionType.Bool,
            "False")
    };

    /// <summary>
    /// Modbus 组起始地址格式为「站号.区标识」,如 "1.400001"。
    /// 区标识首字符决定寄存器类型:
    ///   4 = 保持寄存器(40001 短 / 400001 长)
    ///   3 = 输入寄存器(30001 / 300001)
    ///   1 = 线圈(10001 / 100001)
    ///   0 = 线圈(1 + 偏移)
    /// </summary>
    public string BuildAddress(string groupStart, string varOffset, DeviceConfig device)
    {
        if (string.IsNullOrWhiteSpace(groupStart) || string.IsNullOrWhiteSpace(varOffset))
        {
            return string.Empty;
        }

        var parts = groupStart.Split('.');
        if (parts.Length != 2 || string.IsNullOrEmpty(parts[1]))
        {
            return string.Empty;
        }

        if (!int.TryParse(varOffset.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
        {
            return string.Empty;
        }

        if (offset < 0)
        {
            return string.Empty;
        }

        var station = parts[0];

        // 缺省必须是 false,与 Options 里声明的默认值 "False" 一致。
        // 原来两处相反:"新建设备"按 Options 得到 False(长地址 400001+),
        // 而配置里**没写这个键**的老文件按 true 走(短地址 40001+),
        // 同一份组态换个入口就得到不同的地址串,排查时极难看出差别。
        // (两种写法最终都会被 acquisition 的 NormalizeRegister 归一,落到同一个寄存器,
        //  所以这里统一默认值不会改动现场已在用的点位。)
        var isShort = ReadBoolOption(device, "IsShortAddress", false);

        var area = parts[1][0];

        var baseAddress = area switch
        {
            '4' => isShort ? 40001 : 400001,
            '3' => isShort ? 30001 : 300001,
            '1' => isShort ? 10001 : 100001,
            '0' => 1,
            _ => -1
        };

        if (baseAddress < 0)
        {
            return string.Empty;
        }

        var finalAddress = baseAddress + offset;

        return station + "." + finalAddress.ToString(CultureInfo.InvariantCulture);
    }

    public string? ValidateAddress(string fullAddress, DeviceConfig device)
    {
        if (string.IsNullOrWhiteSpace(fullAddress))
        {
            return "地址为空";
        }

        var parts = fullAddress.Split('.');
        if (parts.Length != 2)
        {
            return "Modbus 地址格式应为「站号.寄存器地址」,如 1.40001";
        }

        if (!int.TryParse(parts[0], out var station) || station < 0 || station > 255)
        {
            return "站号应在 0~255 之间,实际为 " + parts[0];
        }

        if (!int.TryParse(parts[1], out var address) || address < 0)
        {
            return "寄存器地址应为非负整数,实际为 " + parts[1];
        }

        // 短地址最大 65535
        if (address > 65535 && address < 100001)
        {
            return "寄存器地址 " + address + " 超出 16 位范围(最大 65535)";
        }

        return null;
    }

    public string? ValidateOptions(DeviceConfig device)
    {
        // IsShortAddress 有默认值,不是必填
        return null;
    }

    /// <summary>读布尔型 Options,缺省时用 defaultValue。</summary>
    internal static bool ReadBoolOption(DeviceConfig device, string key, bool defaultValue)
    {
        if (!device.Options.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        raw = raw.Trim();

        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        // 兼容 1/0、yes/no、on/off(现场有人这么填)
        if (raw == "1") return true;
        if (raw == "0") return false;

        if (string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "是", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "否", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 认不出来的值退回默认值,而不是悄悄按 false 处理
        return defaultValue;
    }
}
