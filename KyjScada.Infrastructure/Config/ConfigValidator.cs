using KyjScada.Domain.Config;
using KyjScada.Infrastructure.Acquisition;

namespace KyjScada.Infrastructure.Config;

/// <summary>
/// 配置校验器。给配置编辑器做"保存前自检"。
///
/// 为什么必须有:配置错了不会报错,只会**静默读错值**。
/// 老系统踩过的例子:
///  - Scale 配成 0 → 所有读数恒为 0,曲线是一条零线,不报任何异常;
///  - 变量偏移超出组长度 → 该变量永远读不到,但在界面上只是显示 "--";
///  - 变量重名 → 后一个覆盖前一个,界面上少一个测点却不知道为什么;
///  - 报警启用但阈值是 0 → 该点一旦有值就报警,现场一片红。
///
/// 这些都不能靠"跑起来看看"发现,必须在保存前静态查出来。
/// </summary>
public static class ConfigValidator
{
    /// <summary>已知的变量类型(与采集层的解析分支一一对应)。</summary>
    public static readonly string[] KnownTypes =
    {
        "Bool", "Byte", "SByte",
        "Short", "UShort", "Word",
        "Int", "Int32", "UInt", "UInt32", "DWord",
        "Float", "Single",
        "Double",
        "Long", "Int64", "ULong", "UInt64"
    };

    /// <summary>
    /// 校验整个项目。返回问题清单,空列表表示可用。
    /// 消息里带"设备/组/变量"路径,现场改配置能直接定位。
    /// </summary>
    public static List<string> Validate(ProjectConfig project)
    {
        var problems = new List<string>();

        if (project is null) return problems;

        if (string.IsNullOrWhiteSpace(project.ProjectName))
        {
            problems.Add("项目名称不能为空");
        }

        var devices = project.AllDevices().ToList();

        if (devices.Count == 0)
        {
            problems.Add("项目中没有任何设备");
        }

        // 设备名重复也检查:现场按设备名区分,重名会看错
        var deviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in devices)
        {
            if (string.IsNullOrWhiteSpace(d.DeviceName))
            {
                problems.Add("存在设备名称为空的设备");
            }
            else if (!deviceNames.Add(d.DeviceName))
            {
                problems.Add("设备名称重复: " + d.DeviceName);
            }

            problems.AddRange(ValidateDevice(d));
        }

        return problems;
    }

    /// <summary>校验单个设备(含其全部组与变量)。</summary>
    public static List<string> ValidateDevice(DeviceConfig device)
    {
        var problems = new List<string>();

        if (device is null) return problems;

        var prefix = string.IsNullOrWhiteSpace(device.DeviceName)
            ? "(未命名设备)"
            : device.DeviceName;

        if (device.GroupList is null || device.GroupList.Count == 0)
        {
            problems.Add(prefix + ":没有配置任何通讯组");
            return problems;
        }

        // 地址/偏移/组长度这一层交给采集层校验,规则只维护一份
        foreach (var p in ModbusAcquisitionService.ValidateLayout(device))
        {
            problems.Add(prefix + ": " + p);
        }

        // 变量名在"设备内"唯一(不同设备可以重名)
        var varNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in device.GroupList)
        {
            if (string.IsNullOrWhiteSpace(g.GroupName))
            {
                problems.Add(prefix + ": 存在组名为空的通讯组");
            }

            if (g.VarList is null) continue;

            if (g.VarList.Count == 0)
            {
                problems.Add(prefix + " / 组 " + g.GroupName + ": 组内没有变量");
            }

            foreach (var v in g.VarList)
            {
                var where = prefix + " / " + g.GroupName + " / "
                    + (string.IsNullOrWhiteSpace(v.VarName) ? "(未命名变量)" : v.VarName);

                if (string.IsNullOrWhiteSpace(v.VarName))
                {
                    problems.Add(prefix + " / 组 " + g.GroupName + ": 存在变量名为空的变量");
                }
                else if (!varNames.Add(v.VarName))
                {
                    problems.Add(where + ": 变量名重复(同一设备内不能重名)");
                }

                if (!IsKnownType(v.VarType))
                {
                    problems.Add(where + ": 未知数据类型 (" + v.VarType + ")");
                }

                // Scale=0 是"静默读零"的头号原因
                if (v.Scale == 0f)
                {
                    problems.Add(where + ": Scale 为 0,读数会恒为 0(应为 1)");
                }

                if (v.HighAlarmEnable && !IsFiniteThreshold(v.HighAlarmValue))
                {
                    problems.Add(where + ": 启用了上限报警但阈值无效(0 或非数值)");
                }

                if (v.LowAlarmEnable && !IsFiniteThreshold(v.LowAlarmValue))
                {
                    problems.Add(where + ": 启用了下限报警但阈值无效(0 或非数值)");
                }

                if (v.HighAlarmEnable && v.LowAlarmEnable && v.HighAlarmValue < v.LowAlarmValue)
                {
                    problems.Add(where + ": 上限 " + v.HighAlarmValue
                        + " 小于下限 " + v.LowAlarmValue + "(设置反了)");
                }

                if (v.AlarmDeadband < 0f)
                {
                    problems.Add(where + ": 报警死区不能为负");
                }
            }
        }

        return problems;
    }

    public static bool IsKnownType(string? varType)
    {
        if (string.IsNullOrWhiteSpace(varType)) return false;

        var t = varType.Trim();

        foreach (var k in KnownTypes)
        {
            if (string.Equals(k, t, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>阈值必须是有限数值。0 视为"没配"(与采集层 0=未配置 的约定一致)。</summary>
    private static bool IsFiniteThreshold(float v)
    {
        return !float.IsNaN(v) && !float.IsInfinity(v) && v != 0f;
    }
}