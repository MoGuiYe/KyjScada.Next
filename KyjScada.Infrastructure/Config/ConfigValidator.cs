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

        // 变量名 → 首次出现的设备名(跨设备唯一性检查用)
        var globalVarNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

            // 变量名必须**全项目**唯一。历史库的 VarDict.VarName 是全局唯一键
            // (见 HistoryRepository),两台设备用同一个变量名会写进同一个 VarId ——
            // 趋势图把两台设备的数据画成一条线,报表统计也是合并的,现场根本看不出来。
            // 这里以前只查"同一设备内不重名",等于允许制造这种串台配置。
            var owner = string.IsNullOrWhiteSpace(d.DeviceName) ? "(未命名设备)" : d.DeviceName;

            foreach (var g in d.GroupList ?? new List<GroupConfig>())
            {
                if (g.VarList is null) continue;

                foreach (var v in g.VarList)
                {
                    if (string.IsNullOrWhiteSpace(v.VarName)) continue;

                    if (globalVarNames.TryGetValue(v.VarName, out var firstOwner))
                    {
                        // 同一设备内的重名 ValidateDevice 已经报过,这里只管跨设备
                        if (!string.Equals(firstOwner, owner, StringComparison.OrdinalIgnoreCase))
                        {
                            problems.Add("变量名重复(跨设备): " + v.VarName
                                + " 同时出现在设备「" + firstOwner + "」与「" + owner
                                + "」。历史库按变量名全局唯一,两台设备的数据会写进同一条历史(趋势/报表串台)");
                        }
                    }
                    else
                    {
                        globalVarNames[v.VarName] = owner;
                    }
                }
            }
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

                // Scale=0 是"静默读零"的头号原因;NaN/Infinity 会把所有读数变成 NaN。
                // 这两类必须分开报:前者是"配错了值",后者是"写进去的不是数"。
                if (v.Scale == 0f)
                {
                    problems.Add(where + ": Scale 为 0,读数会恒为 0(应为 1)");
                }
                else if (!float.IsFinite(v.Scale))
                {
                    problems.Add(where + ": Scale 为 NaN/Infinity,所有读数都会变成 NaN");
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

                // 死区是"穿越阈值后还要多走多少才报警"的比较基准:
                // NaN 时 (值 - 阈值) > NaN 恒为 false → 永远不报警,看着配了其实没有。
                if (!float.IsFinite(v.AlarmDeadband))
                {
                    problems.Add(where + ": 报警死区不是有效数值(NaN/Infinity),报警比较会恒不成立");
                }
                else if (v.AlarmDeadband < 0f)
                {
                    problems.Add(where + ": 报警死区不能为负");
                }

                // ================= 写入上下限 =================
                // 这两个值直接决定"写入是否被判越界",配错了比不配更危险:
                //   · NaN 时 value > NaN 恒为 false → 任何值都静默放行,看着像"配了保护"其实没有;
                //   · 上限 < 下限时两边都判越界,任何值都写不进去,现场只会以为"功能坏了"。
                if (v.WriteHighLimit is { } wh && (double.IsNaN(wh) || double.IsInfinity(wh)))
                {
                    problems.Add(where + ": 写入上限不是有效数值(NaN/Infinity),越界判定会失效");
                }

                if (v.WriteLowLimit is { } wl && (double.IsNaN(wl) || double.IsInfinity(wl)))
                {
                    problems.Add(where + ": 写入下限不是有效数值(NaN/Infinity),越界判定会失效");
                }

                if (v.WriteHighLimit is { } h2 && v.WriteLowLimit is { } l2
                    && !double.IsNaN(h2) && !double.IsNaN(l2) && h2 < l2)
                {
                    problems.Add(where + ": 写入上限 " + h2 + " 小于写入下限 " + l2 + "(设置反了,任何值都会被判越界)");
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