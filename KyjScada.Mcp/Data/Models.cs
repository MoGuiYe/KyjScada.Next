using System.Text.Json.Serialization;

namespace KyjScada.Mcp.Data;

/// <summary>
/// 根组态容器。settings/*.json 实测为单元素数组:
/// [ { ProjectName, ModbusTCPList[], ModbusRTUList[], ... } ]
/// </summary>
public sealed class ProjectConfig
{
    public string? ProjectName { get; set; }

    public List<TcpDeviceConfig> ModbusTCPList { get; set; } = new();

    public List<DeviceConfig> ModbusRTUList { get; set; } = new();
}

/// <summary>Modbus TCP 设备。</summary>
public sealed class TcpDeviceConfig : DeviceConfig
{
    public string? IpAddress { get; set; }

    public int Port { get; set; }
}

/// <summary>设备基类(组态共用字段)。</summary>
public class DeviceConfig
{
    public string? DeviceName { get; set; }

    public bool IsActive { get; set; }

    public List<GroupConfig> GroupList { get; set; } = new();
}

/// <summary>通讯组。</summary>
public sealed class GroupConfig
{
    public string? GroupName { get; set; }

    public string? Start { get; set; }

    public int Length { get; set; }

    public bool IsActive { get; set; }

    public List<VariableConfig> VarList { get; set; } = new();
}

/// <summary>
/// 变量。实测字段来自 ConfigLib/Variable/VariableBase.cs。
/// Comments 是中文位号说明,报警阈值与说明对 AI 判断很有价值。
/// </summary>
public sealed class VariableConfig
{
    public string? VarName { get; set; }

    public string? VarType { get; set; }

    public string? VarAddress { get; set; }

    public string? Comments { get; set; }

    public string? Unit { get; set; }

    public float Scale { get; set; } = 1f;

    public float Offset { get; set; }

    public bool ArchiveEnable { get; set; }

    public bool HighAlarmEnable { get; set; }

    public float HighAlarmValue { get; set; }

    public string? HighAlarmNote { get; set; }

    public bool LowAlarmEnable { get; set; }

    public float LowAlarmValue { get; set; }

    public string? LowAlarmNote { get; set; }
}
