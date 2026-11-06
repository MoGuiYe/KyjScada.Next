namespace KyjScada.Domain.Config;

/// <summary>连接方式。三种,字段差异是字段而非行为,所以不建多态子类。</summary>
public enum ConnectionKind
{
    Tcp = 0,
    Serial = 1,
    Opc = 2
}

/// <summary>
/// 连接参数。
/// 注意 Parity/StopBits 用 int:现场 settings.json 里就是 0/1,
/// 用枚举名会导致读写不兼容。
/// </summary>
public sealed class ConnectionConfig
{
    public ConnectionKind Kind { get; set; } = ConnectionKind.Tcp;

    // ===== TCP =====
    public string? IpAddress { get; set; }
    public int Port { get; set; }

    // ===== 串口 =====
    public string? PortName { get; set; }
    public int BaudRate { get; set; } = 9600;
    public int Parity { get; set; }
    public int DataBits { get; set; } = 8;
    public int StopBits { get; set; } = 1;

    public ConnectionConfig Clone()
    {
        return new ConnectionConfig
        {
            Kind = Kind,
            IpAddress = IpAddress,
            Port = Port,
            PortName = PortName,
            BaudRate = BaudRate,
            Parity = Parity,
            DataBits = DataBits,
            StopBits = StopBits
        };
    }

    /// <summary>人类可读的连接描述,用于界面与日志。</summary>
    public string Describe()
    {
        return Kind switch
        {
            ConnectionKind.Tcp => $"TCP {IpAddress}:{Port}",
            ConnectionKind.Serial => $"串口 {PortName} {BaudRate},{DataBits},{Parity},{StopBits}",
            ConnectionKind.Opc => "OPC",
            _ => "未知"
        };
    }
}
