using KyjScada.Domain.Config;

namespace KyjScada.Domain.Protocol;

/// <summary>
/// Modbus RTU(串口)。地址规则与 TCP 完全相同,只是连接方式不同。
/// 复用 ModbusTcpDescriptor 的地址逻辑,避免两份规则漂移。
/// </summary>
public sealed class ModbusRtuDescriptor : IProtocolDescriptor
{
    private static readonly ModbusTcpDescriptor AddressLogic = new();

    public string Key => "ModbusRTU";

    public string DisplayName => "Modbus RTU";

    public ConnectionKind ConnectionKind => ConnectionKind.Serial;

    public IReadOnlyList<OptionDefinition> Options => new[]
    {
        new OptionDefinition("IsShortAddress", "短地址模式", OptionType.Bool, "False"),
        new OptionDefinition("CycleTimes", "轮询次数", OptionType.Int, "3"),
        new OptionDefinition("CheckCycleTimes", "校验轮询次数", OptionType.Int, "3")
    };

    public string BuildAddress(string groupStart, string varOffset, DeviceConfig device)
    {
        return AddressLogic.BuildAddress(groupStart, varOffset, device);
    }

    public string? ValidateAddress(string fullAddress, DeviceConfig device)
    {
        return AddressLogic.ValidateAddress(fullAddress, device);
    }

    public string? ValidateOptions(DeviceConfig device)
    {
        return null;
    }
}

/// <summary>
/// Modbus RTU Over TCP(串口协议走网络)。连接是 TCP,地址规则同 Modbus。
/// </summary>
public sealed class ModbusRtuOverTcpDescriptor : IProtocolDescriptor
{
    private static readonly ModbusTcpDescriptor AddressLogic = new();

    public string Key => "ModbusRTUOverTCP";

    public string DisplayName => "Modbus RTU Over TCP";

    public ConnectionKind ConnectionKind => ConnectionKind.Tcp;

    public IReadOnlyList<OptionDefinition> Options => new[]
    {
        new OptionDefinition("IsShortAddress", "短地址模式", OptionType.Bool, "False"),
        new OptionDefinition("CycleTimes", "轮询次数", OptionType.Int, "3"),
        new OptionDefinition("CheckCycleTimes", "校验轮询次数", OptionType.Int, "3")
    };

    public string BuildAddress(string groupStart, string varOffset, DeviceConfig device)
    {
        return AddressLogic.BuildAddress(groupStart, varOffset, device);
    }

    public string? ValidateAddress(string fullAddress, DeviceConfig device)
    {
        return AddressLogic.ValidateAddress(fullAddress, device);
    }

    public string? ValidateOptions(DeviceConfig device)
    {
        return null;
    }
}

/// <summary>
/// Modbus ASCII(串口 ASCII 编码)。
/// </summary>
public sealed class ModbusAsciiDescriptor : IProtocolDescriptor
{
    private static readonly ModbusTcpDescriptor AddressLogic = new();

    public string Key => "ModbusASCII";

    public string DisplayName => "Modbus ASCII";

    public ConnectionKind ConnectionKind => ConnectionKind.Serial;

    public IReadOnlyList<OptionDefinition> Options => new[]
    {
        new OptionDefinition("IsShortAddress", "短地址模式", OptionType.Bool, "False")
    };

    public string BuildAddress(string groupStart, string varOffset, DeviceConfig device)
    {
        return AddressLogic.BuildAddress(groupStart, varOffset, device);
    }

    public string? ValidateAddress(string fullAddress, DeviceConfig device)
    {
        return AddressLogic.ValidateAddress(fullAddress, device);
    }

    public string? ValidateOptions(DeviceConfig device)
    {
        return null;
    }
}
