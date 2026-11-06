using KyjScada.Domain.Config;

namespace KyjScada.Domain.Protocol;

/// <summary>
/// OPC DA(经典 COM 版)。地址就是 ItemID,原样透传。
/// 注意:老项目这个协议是空壳,只有 ServerNode/ServerName,
/// 没有任何连接实现。新系统第一版也不实现通讯,只保留配置能力。
/// </summary>
public sealed class OpcDaDescriptor : IProtocolDescriptor
{
    public string Key => "OPCDA";

    public string DisplayName => "OPC DA";

    public ConnectionKind ConnectionKind => ConnectionKind.Opc;

    public IReadOnlyList<OptionDefinition> Options => new[]
    {
        new OptionDefinition("ServerNode", "服务器节点", OptionType.Text, "localhost"),
        new OptionDefinition("ServerName", "服务器名称", OptionType.Text, "", required: true)
    };

    public string BuildAddress(string groupStart, string varOffset, DeviceConfig device)
    {
        // OPC 的 ItemID 不是数值偏移,直接拼接
        if (string.IsNullOrWhiteSpace(groupStart) && string.IsNullOrWhiteSpace(varOffset))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(groupStart))
        {
            return varOffset.Trim();
        }

        if (string.IsNullOrWhiteSpace(varOffset))
        {
            return groupStart.Trim();
        }

        return groupStart.Trim() + "." + varOffset.Trim();
    }

    public string? ValidateAddress(string fullAddress, DeviceConfig device)
    {
        return string.IsNullOrWhiteSpace(fullAddress) ? "ItemID 为空" : null;
    }

    public string? ValidateOptions(DeviceConfig device)
    {
        if (!device.Options.TryGetValue("ServerName", out var serverName)
            || string.IsNullOrWhiteSpace(serverName))
        {
            return "OPC DA 必须填写服务器名称";
        }

        return null;
    }
}

/// <summary>
/// OPC UA。地址是 NodeId,原样透传。
/// 同样只保留配置能力,第一版不实现通讯。
/// </summary>
public sealed class OpcUaDescriptor : IProtocolDescriptor
{
    public string Key => "OPCUA";

    public string DisplayName => "OPC UA";

    public ConnectionKind ConnectionKind => ConnectionKind.Opc;

    public IReadOnlyList<OptionDefinition> Options => new[]
    {
        new OptionDefinition("ServerUrl", "服务器地址", OptionType.Text, "", required: true)
    };

    public string BuildAddress(string groupStart, string varOffset, DeviceConfig device)
    {
        if (string.IsNullOrWhiteSpace(varOffset))
        {
            return string.IsNullOrWhiteSpace(groupStart) ? string.Empty : groupStart.Trim();
        }

        return varOffset.Trim();
    }

    public string? ValidateAddress(string fullAddress, DeviceConfig device)
    {
        return string.IsNullOrWhiteSpace(fullAddress) ? "NodeId 为空" : null;
    }

    public string? ValidateOptions(DeviceConfig device)
    {
        if (!device.Options.TryGetValue("ServerUrl", out var url)
            || string.IsNullOrWhiteSpace(url))
        {
            return "OPC UA 必须填写服务器地址";
        }

        return null;
    }
}
