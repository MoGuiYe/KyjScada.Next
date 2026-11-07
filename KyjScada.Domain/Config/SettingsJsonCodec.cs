using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KyjScada.Domain.Config;

/// <summary>
/// settings.json 编解码器。
///
/// 兼容目标:现场 3 个文件,格式为顶层数组 [ { ProjectName, XxxList... }, ... ],
/// 每个协议一个键,键名固定 11 个。
///
/// 实测注意:现场文件的顶层数组里有 3 个项目,但只有 1 个有设备,
/// 另外 2 个是空壳(老编辑器遗留)。因此 LoadFile 返回全部,
/// 调用方要用 ActiveProject 取真正有数据的那个。
/// </summary>
public static class SettingsJsonCodec
{
    /// <summary>settings.json 的 11 个协议键,顺序与老文件一致。</summary>
    public static readonly string[] CanonicalKeys =
    {
        "SiemensList",
        "MelsecList",
        "MelsecFXSerialList",
        "OmronList",
        "OmronHostlinkList",
        "ModbusRTUList",
        "ModbusASCIIList",
        "ModbusTCPList",
        "ModbusRTUOverTCPList",
        "OPCDAList",
        "OPCUAList"
    };

    public static string KeyToProtocol(string listKey)
    {
        return listKey.EndsWith("List", StringComparison.Ordinal)
            ? listKey[..^4]
            : listKey;
    }

    public static string ProtocolToKey(string protocolKey)
    {
        return protocolKey.EndsWith("List", StringComparison.Ordinal)
            ? protocolKey
            : protocolKey + "List";
    }

    /// <summary>
    /// 从多个项目里取真正有设备的那个。
    /// 老文件常有 2~3 个空壳项目,直接取 [0] 会拿到空项目。
    /// </summary>
    public static ProjectConfig? ActiveProject(IEnumerable<ProjectConfig> projects)
    {
        return projects.FirstOrDefault(p => p.AllDevices().Any())
            ?? projects.FirstOrDefault();
    }

    // ================= 读 =================

    public static List<ProjectConfig> Parse(string json)
    {
        var result = new List<ProjectConfig>();

        var root = JToken.Parse(json);
        var projects = root is JArray arr ? arr : new JArray(root);

        foreach (var token in projects)
        {
            if (token is not JObject obj) continue;
            result.Add(ParseProject(obj));
        }

        return result;
    }

    private static ProjectConfig ParseProject(JObject obj)
    {
        var project = new ProjectConfig
        {
            ProjectName = obj.Value<string>("ProjectName") ?? ""
        };

        foreach (var prop in obj.Properties())
        {
            if (prop.Name == "ProjectName") continue;
            if (prop.Value is not JArray list) continue;

            var protocolKey = KeyToProtocol(prop.Name);
            var devices = new List<DeviceConfig>();

            foreach (var devToken in list)
            {
                if (devToken is not JObject devObj) continue;
                devices.Add(ParseDevice(devObj, protocolKey));
            }

            project.DevicesByProtocol[protocolKey] = devices;
        }

        return project;
    }

    /// <summary>
    /// 解析老系统的 DataFormat 字段。
    /// 老枚举:0=ABCD 1=BADC 2=CDAB 3=DCBA。空值/非法值一律按 ABCD(与老系统默认一致)。
    /// 这里必须严格对齐老枚举数值,否则 CDAB 会被错当成 BADC,读数全错。
    /// </summary>
    private static WordOrder ParseWordOrder(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return WordOrder.Abcd;

        var text = raw.Trim();

        // 先按数字解析(老配置实际存的是数字)。
        if (int.TryParse(text, out var n))
        {
            switch (n)
            {
                case 1: return WordOrder.Badc;
                case 2: return WordOrder.Cdab;
                case 3: return WordOrder.Dcba;
                default: return WordOrder.Abcd;
            }
        }

        // 兼容直接写枚举名的配置。
        switch (text.ToUpperInvariant())
        {
            case "BADC": return WordOrder.Badc;
            case "CDAB": return WordOrder.Cdab;
            case "DCBA": return WordOrder.Dcba;
            default: return WordOrder.Abcd;
        }
    }

    private static DeviceConfig ParseDevice(JObject obj, string protocolKey)
    {
        var device = new DeviceConfig
        {
            ProtocolKey = protocolKey,
            DeviceName = obj.Value<string>("DeviceName") ?? "",
            IsActive = obj.Value<bool?>("IsActive") ?? true
        };

        var conn = new ConnectionConfig();

        var ip = obj.Value<string>("IpAddress");
        if (!string.IsNullOrEmpty(ip))
        {
            conn.Kind = ConnectionKind.Tcp;
            conn.IpAddress = ip;
            conn.Port = obj.Value<int?>("Port") ?? 502;
        }
        else
        {
            var portName = obj.Value<string>("PortName");
            if (!string.IsNullOrEmpty(portName))
            {
                conn.Kind = ConnectionKind.Serial;
                conn.PortName = portName;
                conn.BaudRate = obj.Value<int?>("BaudRate") ?? 9600;
                conn.Parity = obj.Value<int?>("Parity") ?? 0;
                conn.DataBits = obj.Value<int?>("DataBits") ?? 8;
                conn.StopBits = obj.Value<int?>("StopBits") ?? 1;
            }
        }

        device.Connection = conn;

        // 老系统的 DataFormat 是数字(0=ABCD 1=BADC 2=CDAB 3=DCBA),空值走 ABCD。
        // 必须显式映射到 WordOrder,否则 32/64 位数据会按错误的字序拼接。
        device.WordOrder = ParseWordOrder(obj.Value<string>("DataFormat"));

        foreach (var prop in obj.Properties())
        {
            if (IsStructuralField(prop.Name)) continue;
            if (prop.Value is JArray or JObject) continue;

            var text = prop.Value.Type == JTokenType.Null
                ? ""
                : Convert.ToString(prop.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";

            device.Options[prop.Name] = text;
        }

        if (obj["GroupList"] is JArray groupList)
        {
            foreach (var grpToken in groupList)
            {
                if (grpToken is not JObject grpObj) continue;
                device.GroupList.Add(ParseGroup(grpObj));
            }
        }

        return device;
    }

    private static GroupConfig ParseGroup(JObject obj)
    {
        var group = new GroupConfig
        {
            GroupName = obj.Value<string>("GroupName") ?? "",
            IsActive = obj.Value<bool?>("IsActive") ?? true,
            IsWritable = obj.Value<bool?>("IsWritable") ?? false,
            Start = obj.Value<string>("Start"),
            Length = obj.Value<int?>("Length") ?? 0
        };

        if (obj["VarList"] is JArray varList)
        {
            foreach (var varToken in varList)
            {
                if (varToken is not JObject varObj) continue;
                group.VarList.Add(ParseVariable(varObj));
            }
        }

        return group;
    }

    private static VariableConfig ParseVariable(JObject obj)
    {
        var scale = obj.Value<float?>("Scale");

        // Scale 为 0 或缺省都视为 1,否则读数恒为 0 且不报错
        if (scale is null or 0f) scale = 1f;

        return new VariableConfig
        {
            Start = obj.Value<string>("Start"),
            VarName = obj.Value<string>("VarName") ?? "",
            VarType = obj.Value<string>("VarType") ?? "Float",
            VarAddress = obj.Value<string>("VarAddress"),
            Comments = obj.Value<string>("Comments"),
            Unit = obj.Value<string>("Unit"),
            Scale = scale.Value,
            Offset = obj.Value<float?>("Offset") ?? 0f,
            ArchiveEnable = obj.Value<bool?>("ArchiveEnable") ?? false,
            HighAlarmEnable = obj.Value<bool?>("HighAlarmEnable") ?? false,
            HighAlarmValue = obj.Value<float?>("HighAlarmValue") ?? 0f,
            HighAlarmNote = obj.Value<string>("HighAlarmNote"),
            LowAlarmEnable = obj.Value<bool?>("LowAlarmEnable") ?? false,
            LowAlarmValue = obj.Value<float?>("LowAlarmValue") ?? 0f,
            LowAlarmNote = obj.Value<string>("LowAlarmNote"),
            AlarmDeadband = obj.Value<float?>("AlarmDeadband") ?? 0f
        };
    }

    private static bool IsStructuralField(string name)
    {
        return name is "DeviceName" or "IsActive" or "GroupList"
            or "IpAddress" or "Port"
            or "PortName" or "BaudRate" or "Parity" or "DataBits" or "StopBits";
    }

    // ================= 写 =================

    public static string ToJson(IEnumerable<ProjectConfig> projects, bool indented = true)
    {
        var array = new JArray();

        foreach (var project in projects)
        {
            array.Add(ToJObject(project));
        }

        return array.ToString(indented ? Formatting.Indented : Formatting.None);
    }

    private static JObject ToJObject(ProjectConfig project)
    {
        var obj = new JObject { ["ProjectName"] = project.ProjectName };

        foreach (var listKey in CanonicalKeys)
        {
            var protocolKey = KeyToProtocol(listKey);
            var devices = project.GetDevices(protocolKey);

            var arr = new JArray();
            foreach (var device in devices)
            {
                arr.Add(ToDeviceObject(device));
            }

            obj[listKey] = arr;
        }

        foreach (var kv in project.DevicesByProtocol)
        {
            var listKey = ProtocolToKey(kv.Key);
            if (obj.ContainsKey(listKey)) continue;

            var arr = new JArray();
            foreach (var device in kv.Value)
            {
                arr.Add(ToDeviceObject(device));
            }

            obj[listKey] = arr;
        }

        return obj;
    }

    private static JObject ToDeviceObject(DeviceConfig device)
    {
        var obj = new JObject();

        if (device.Connection.Kind == ConnectionKind.Tcp)
        {
            obj["IpAddress"] = device.Connection.IpAddress ?? "";
            obj["Port"] = device.Connection.Port;
        }
        else if (device.Connection.Kind == ConnectionKind.Serial)
        {
            obj["PortName"] = device.Connection.PortName ?? "";
            obj["BaudRate"] = device.Connection.BaudRate;
            obj["Parity"] = device.Connection.Parity;
            obj["DataBits"] = device.Connection.DataBits;
            obj["StopBits"] = device.Connection.StopBits;
        }

        foreach (var kv in device.Options)
        {
            obj[kv.Key] = kv.Value;
        }

        // 字节序显式落盘,保证往返保真(即使 Options 里没有 DataFormat)。
        obj["DataFormat"] = ((int)device.WordOrder).ToString();

        var groups = new JArray();
        foreach (var group in device.GroupList)
        {
            groups.Add(ToGroupObject(group));
        }
        obj["GroupList"] = groups;

        obj["DeviceName"] = device.DeviceName;
        obj["IsActive"] = device.IsActive;

        return obj;
    }

    private static JObject ToGroupObject(GroupConfig group)
    {
        var obj = new JObject
        {
            ["Start"] = group.Start ?? "",
            ["Length"] = group.Length
        };

        var vars = new JArray();
        foreach (var v in group.VarList)
        {
            vars.Add(ToVariableObject(v));
        }

        obj["VarList"] = vars;
        obj["GroupName"] = group.GroupName;
        obj["IsActive"] = group.IsActive;

        // 只在开启时才写该字段:老配置保持原样,避免把 129 个变量的文件全改一遍
        if (group.IsWritable) obj["IsWritable"] = true;

        return obj;
    }

    private static JObject ToVariableObject(VariableConfig v)
    {
        var obj = new JObject
        {
            ["Start"] = v.Start ?? "",
            ["VarName"] = v.VarName,
            ["VarType"] = v.VarType,
            ["VarAddress"] = v.VarAddress ?? "",
            ["Comments"] = v.Comments ?? "",
            ["Scale"] = v.Scale,
            ["Offset"] = v.Offset,
            ["ArchiveEnable"] = v.ArchiveEnable
        };

        if (!string.IsNullOrEmpty(v.Unit)) obj["Unit"] = v.Unit;

        if (v.HighAlarmEnable || v.HighAlarmValue != 0f)
        {
            obj["HighAlarmEnable"] = v.HighAlarmEnable;
            obj["HighAlarmValue"] = v.HighAlarmValue;
            obj["HighAlarmNote"] = v.HighAlarmNote ?? "";
        }

        if (v.LowAlarmEnable || v.LowAlarmValue != 0f)
        {
            obj["LowAlarmEnable"] = v.LowAlarmEnable;
            obj["LowAlarmValue"] = v.LowAlarmValue;
            obj["LowAlarmNote"] = v.LowAlarmNote ?? "";
        }

        if (v.AlarmDeadband != 0f) obj["AlarmDeadband"] = v.AlarmDeadband;

        return obj;
    }

    // ================= 文件 =================

    public static List<ProjectConfig> LoadFile(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return Parse(text);
    }

    /// <summary>加载结果:合并后的项目 + 参与合并的文件名 + 设备→来源文件。</summary>
    public sealed record LoadedSettings(
        ProjectConfig Project,
        List<string> Files,
        Dictionary<string, string> DeviceFiles);

    /// <summary>
    /// 加载整个配置目录。
    ///
    /// 现场配置是**按协议分文件**的:ModbusTCP.json / ModbusRTU.json / ModbusRTUOverTCP.json,
    /// 以后还会有 OPC.json、西门子、三菱等。只读其中一个文件等于把其它设备全丢了。
    ///
    /// 合并规则:各文件的设备按协议键并入同一个项目;项目名取第一个非空的。
    /// 文件名排序后加载,保证顺序稳定(同一份目录每次加载结果一致)。
    /// </summary>
    public static LoadedSettings LoadDirectory(string dir)
    {
        var merged = new ProjectConfig();
        var files = new List<string>();

        // 设备名 → 来源文件(改字节序时要写回正确的那个文件)
        var deviceFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(dir))
        {
            return new LoadedSettings(merged, files, deviceFiles);
        }

        var paths = Directory.GetFiles(dir, "*.json")
            .Where(f => !f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var path in paths)
        {
            List<ProjectConfig> projects;

            try
            {
                projects = LoadFile(path);
            }
            catch
            {
                // 单个文件坏了不该让整个系统起不来:跳过并在 Files 里体现不出来即可
                continue;
            }

            var project = ActiveProject(projects);

            if (project is null) continue;

            if (string.IsNullOrWhiteSpace(merged.ProjectName))
            {
                merged.ProjectName = project.ProjectName;
            }

            var added = false;

            foreach (var kv in project.DevicesByProtocol)
            {
                if (kv.Value is null || kv.Value.Count == 0) continue;

                if (!merged.DevicesByProtocol.TryGetValue(kv.Key, out var list))
                {
                    list = new List<DeviceConfig>();
                    merged.DevicesByProtocol[kv.Key] = list;
                }

                foreach (var d in kv.Value)
                {
                    list.Add(d);

                    // 记住设备来自哪个文件:改字节序时要写回它自己的文件,
                    // 否则合并后无法判断该写哪一个(重名设备取第一个)
                    if (!string.IsNullOrWhiteSpace(d.DeviceName) && !deviceFiles.ContainsKey(d.DeviceName))
                    {
                        deviceFiles[d.DeviceName] = path;
                    }
                }

                added = true;
            }

            if (added) files.Add(Path.GetFileName(path));
        }

        return new LoadedSettings(merged, files, deviceFiles);
    }

    /// <summary>
    /// 把某台设备的字节序写回它所在的配置文件。
    ///
    /// 为什么字节序要落盘而不是只存内存:
    /// 它决定 32/64 位数据怎么拼,配错了所有浮点读数都是乱值,属于**设备参数**,
    /// 必须跟设备配置一起持久化;而"当前看哪台设备"只是界面偏好,另存。
    /// </summary>
    public static bool TryUpdateDeviceWordOrder(string filePath, string deviceName, WordOrder order)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;

        try
        {
            var projects = LoadFile(filePath);
            var project = ActiveProject(projects);

            if (project is null) return false;

            var device = project.AllDevices()
                .FirstOrDefault(d => string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

            if (device is null) return false;

            device.WordOrder = order;

            SaveFile(filePath, projects);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 保存配置。**原子写 + 自动备份**。
    ///
    /// 为什么必须备份:配置文件是现场唯一的一份参数来源,
    /// 写坏了(断电/磁盘满/手误)整个系统就起不来,而且没法回滚。
    /// 先留 .bak,再写 .tmp,最后原子替换 —— 任何一步失败都不会损坏原文件。
    /// </summary>
    public static void SaveFile(string path, IEnumerable<ProjectConfig> projects)
    {
        var json = ToJson(projects);
        var bytes = new UTF8Encoding(false).GetBytes(json);

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);

        if (File.Exists(path))
        {
            // 备份上一次成功保存的内容(覆盖更早的 .bak)
            try
            {
                File.Copy(path, path + ".bak", true);
            }
            catch
            {
                // 备份失败不阻断保存 —— 但保存本身仍走原子替换
            }

            try
            {
                File.Replace(tmp, path, null);
            }
            catch
            {
                // 某些文件系统不支持 Replace(或目标被占用)。先尝试覆盖式移动:
                // Windows 上 MoveFileEx(替换已存在) 在同卷内是原子的,不会出现"目标暂时不存在"。
                try
                {
                    File.Move(tmp, path, overwrite: true);
                }
                catch
                {
                    // 跨卷或被独占时只能"删除后移动",这一步天然不原子。
                    // 删掉之后若移动失败,必须用 .bak 顶回去 ——
                    // 否则现场会看到组态文件凭空消失,下次启动直接没有参数。
                    try
                    {
                        File.Delete(path);
                        File.Move(tmp, path);
                    }
                    catch
                    {
                        RestoreBackup(path);

                        throw;
                    }
                }
            }
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    /// <summary>
    /// 保存失败后的兜底:把上一次的 .bak 顶回原位置。
    /// 只是"尽量别把现场搞成没有配置文件",回滚本身失败就只能把异常交给调用方提示。
    /// </summary>
    private static void RestoreBackup(string path)
    {
        var bak = path + ".bak";

        try
        {
            if (File.Exists(bak)) File.Copy(bak, path, overwrite: true);
        }
        catch
        {
            // 回滚失败:保留原异常(调用方会提示"保存失败"),此处不再抛
        }
    }
}
