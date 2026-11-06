namespace KyjScada.Domain.Protocol;

/// <summary>
/// 协议注册表。**加协议只需在这里加一行**。
///
/// 这是「通用性」的落点:老方案加协议要改 6 个文件,
/// 现在只需写一个描述符类 + 在下面字典里加一行。
/// </summary>
public static class ProtocolRegistry
{
    private static readonly Dictionary<string, IProtocolDescriptor> _descriptors =
        new(StringComparer.OrdinalIgnoreCase);

    static ProtocolRegistry()
    {
        Register(new SiemensDescriptor());
        Register(new MelsecDescriptor());
        Register(new MelsecFXSerialDescriptor());
        Register(new OmronDescriptor());
        Register(new OmronHostlinkDescriptor());
        Register(new ModbusRtuDescriptor());
        Register(new ModbusAsciiDescriptor());
        Register(new ModbusTcpDescriptor());
        Register(new ModbusRtuOverTcpDescriptor());
        Register(new OpcDaDescriptor());
        Register(new OpcUaDescriptor());
    }

    /// <summary>注册一个协议描述符。重复注册同 Key 会覆盖。</summary>
    public static void Register(IProtocolDescriptor descriptor)
    {
        _descriptors[descriptor.Key] = descriptor;
    }

    /// <summary>按协议键取描述符。找不到返回 null。</summary>
    public static IProtocolDescriptor? Find(string protocolKey)
    {
        return _descriptors.TryGetValue(protocolKey, out var d) ? d : null;
    }

    /// <summary>全部已注册协议。</summary>
    public static IReadOnlyList<IProtocolDescriptor> All()
    {
        return _descriptors.Values.ToList();
    }

    /// <summary>全部协议键。</summary>
    public static IReadOnlyList<string> AllKeys()
    {
        return _descriptors.Keys.ToList();
    }

    /// <summary>
    /// 计算设备下所有变量的完整地址。
    /// 组起始 + 变量偏移 -> 完整地址。
    /// </summary>
    public static string ResolveAddress(string protocolKey, string groupStart, string varOffset, Config.DeviceConfig device)
    {
        var descriptor = Find(protocolKey);

        if (descriptor is null)
        {
            return string.Empty;
        }

        return descriptor.BuildAddress(groupStart, varOffset, device);
    }
}
