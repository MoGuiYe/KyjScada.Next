namespace KyjScada.Domain.Config;

/// <summary>
/// 变量配置。全系统唯一的变量模型。
///
/// 血泪教训(来自 KYJ 老项目):
///  - Scale 默认必须是 1f。若为 0,所有测点读数恒为 0,曲线是一条零线且不报错。
///  - 报警必须用显式状态位做状态机,不能靠上一帧值推导。
/// </summary>
public sealed class VariableConfig
{
    /// <summary>组内偏移,如 "0" / "10"。完整地址由协议描述符合成。</summary>
    public string? Start { get; set; }

    public string VarName { get; set; } = "";

    public string VarType { get; set; } = "Float";

    /// <summary>
    /// 已解析的完整地址,如 "1.40001"。
    /// 注意与 Start 并存:Start 是组内偏移,VarAddress 是导出时算好的成品。
    /// 老配置文件两个字段都在,缺一个就会破坏往返保真。
    /// </summary>
    public string? VarAddress { get; set; }

    public string? Comments { get; set; }

    public string? Unit { get; set; }

    /// <summary>线性比例系数。默认 1f,绝不为 0。</summary>
    public float Scale { get; set; } = 1f;

    public float Offset { get; set; }

    public bool ArchiveEnable { get; set; }

    public bool HighAlarmEnable { get; set; }

    public float HighAlarmValue { get; set; }

    public string? HighAlarmNote { get; set; }

    public bool LowAlarmEnable { get; set; }

    public float LowAlarmValue { get; set; }

    public string? LowAlarmNote { get; set; }

    /// <summary>
    /// 报警死区。0 = 不启用。
    /// 实测老系统报警触发 181 次、消除 383 次,液位在阈值附近反复穿越。
    /// 启用后:超上限报警,必须降回 上限-死区 才消除。
    /// </summary>
    public float AlarmDeadband { get; set; }

    public VariableConfig Clone()
    {
        return (VariableConfig)MemberwiseClone();
    }
}

/// <summary>
/// 多寄存器字节序。对应老系统的 DataFormat。
/// ABCD = 大端(默认);CDAB = 字交换;BADC = 字节交换;DCBA = 小端。
/// </summary>
public enum WordOrder
{
    /// <summary>大端。老系统 DataFormat.ABCD = 0,也是其默认值。</summary>
    Abcd = 0,

    /// <summary>字节交换。老系统 DataFormat.BADC = 1。</summary>
    Badc = 1,

    /// <summary>字交换。老系统 DataFormat.CDAB = 2,现场模拟器实测就是这个。</summary>
    Cdab = 2,

    /// <summary>小端。老系统 DataFormat.DCBA = 3。</summary>
    Dcba = 3
}

/// <summary>通讯组。</summary>
public sealed class GroupConfig
{
    public string GroupName { get; set; } = "";

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// 该组的变量是否允许从界面向下写(设定值/参数组用)。
    ///
    /// 为什么必须显式声明:Modbus 里保持寄存器和线圈在协议层面**都是可写的**,
    /// 但"实时值"组里的寄存器是设备的测量结果,写进去毫无意义甚至会干扰设备。
    /// 靠"地址区可写"判断会把 130 多个测量点全暴露成可写,是危险的默认。
    /// 因此默认 false,只有明确标记为可写的组才出现在参数设置页。
    /// </summary>
    public bool IsWritable { get; set; }

    /// <summary>组起始地址,如 "D100" / "1.400001" / "DB1.0"。</summary>
    public string? Start { get; set; }

    /// <summary>组长度,单位是 16 位寄存器(不是字节)。</summary>
    public int Length { get; set; }

    public List<VariableConfig> VarList { get; set; } = new();

    public GroupConfig Clone()
    {
        return new GroupConfig
        {
            GroupName = GroupName,
            IsActive = IsActive,
            IsWritable = IsWritable,
            Start = Start,
            Length = Length,
            VarList = VarList.Select(v => v.Clone()).ToList()
        };
    }
}

/// <summary>
/// 设备配置。11 种协议只有这一个类。
/// 协议特有参数放 Options,不再为每种协议建一套类。
/// </summary>
public sealed class DeviceConfig
{
    public string DeviceName { get; set; } = "";

    public bool IsActive { get; set; } = true;

    /// <summary>协议键,如 "ModbusTCP" / "Siemens" / "OPCUA"。序列化时不落盘,由所在列表推断。</summary>
    public string ProtocolKey { get; set; } = "";

    public ConnectionConfig Connection { get; set; } = new();

    /// <summary>
    /// 协议特有参数。键名由 IProtocolDescriptor.Options 定义并校验。
    /// 替代老方案「借 Comments 字段存参数」——那会导致中文备注被覆盖、
    /// 短地址误判、地址偏移 36 万。
    /// </summary>
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<GroupConfig> GroupList { get; set; } = new();

    /// <summary>
    /// 多寄存器字节序。决定 32/64 位数据怎么拼。
    /// 实测现场模拟器存的是 CDAB(字交换),而老系统默认 ABCD ——
    /// 硬编码任一顺序都会让一半设备读数全错,所以必须做成配置。
    /// </summary>
    public WordOrder WordOrder { get; set; } = WordOrder.Abcd;

    public DeviceConfig Clone()
    {
        return new DeviceConfig
        {
            DeviceName = DeviceName,
            IsActive = IsActive,
            ProtocolKey = ProtocolKey,
            Connection = Connection.Clone(),
            Options = new Dictionary<string, string>(Options, StringComparer.OrdinalIgnoreCase),
            WordOrder = WordOrder,
            GroupList = GroupList.Select(g => g.Clone()).ToList()
        };
    }
}

/// <summary>
/// 项目配置。设备按协议分组,序列化时展开成 settings.json 的 11 个键。
/// </summary>
public sealed class ProjectConfig
{
    public string ProjectName { get; set; } = "";

    /// <summary>
    /// 键 = 协议键(如 "ModbusTCP"),值 = 该协议下的设备列表。
    /// 用字典而不是 11 个强类型 List,这样加协议不用改这个类。
    /// </summary>
    public Dictionary<string, List<DeviceConfig>> DevicesByProtocol { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public List<DeviceConfig> GetDevices(string protocolKey)
    {
        return DevicesByProtocol.TryGetValue(protocolKey, out var list)
            ? list
            : new List<DeviceConfig>();
    }

    public IEnumerable<DeviceConfig> AllDevices()
    {
        return DevicesByProtocol.Values.SelectMany(x => x);
    }

    public IEnumerable<VariableConfig> AllVariables()
    {
        return AllDevices().SelectMany(d => d.GroupList).SelectMany(g => g.VarList);
    }

    public ProjectConfig Clone()
    {
        var copy = new ProjectConfig { ProjectName = ProjectName };

        foreach (var kv in DevicesByProtocol)
        {
            copy.DevicesByProtocol[kv.Key] = kv.Value.Select(d => d.Clone()).ToList();
        }

        return copy;
    }
}
