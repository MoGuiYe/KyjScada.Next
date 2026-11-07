using KyjScada.Domain.Config;

namespace KyjScada.Runtime;

/// <summary>急停/点动页的一个候选点。</summary>
public sealed record JogPoint(string GroupName, string VarName, string VarType, string Comments)
{
    /// <summary>Bool 点才能点动(开/关两态)。数值点只能强制写。</summary>
    public bool IsBool => string.Equals(VarType?.Trim(), "bool", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => VarName;
}

/// <summary>
/// 点动/急停候选集的判定规则。
///
/// 这里之所以独立成模块而不是留在界面代码里,是因为它是**安全逻辑**:
/// "哪些点允许被点动/急停",一旦判错就是把命令写到测量值寄存器上。
/// 抽出来之后可以脱离 WPF 单测,也能被 MCP 的写工具复用。
/// </summary>
public static class JogPoints
{
    /// <summary>
    /// 该组是否开放写入。
    ///
    /// 顺序:配置显式标记(<see cref="GroupConfig.IsWritable"/>)优先;
    /// 老配置没有这个字段时按组名约定兜底 —— "设定值/参数"的语义是明确的。
    /// **实时测量值组绝不能开放写入**:那是设备的测量结果,写进去会干扰设备。
    /// </summary>
    public static bool IsWritableGroup (GroupConfig g)
    {
        if (g is null) return false;

        if (g.IsWritable) return true;

        var name = g.GroupName ?? "";

        // 中文按词包含判断即可 —— 「设定值」「参数」的语义是明确的。
        if (name.Contains ("设定") || name.Contains ("参数")) return true;

        // 英文缩写必须按**词边界**判断。以前是 Contains("Set")/Contains("SP"),
        // 于是 Offset / Reset / SPEED 全都被当成设定值组 —— 测量量组被错开放写入,
        // 而这个判断的结果直接决定"哪些点允许被写"。
        return HasWord (name, "set") || HasWord (name, "setting") || HasWord (name, "settings")
            || HasWord (name, "param") || HasWord (name, "params") || HasWord (name, "sp");
    }

    /// <summary>
    /// 名称里是否含有独立的词:按"非字母数字"或"小写→大写(驼峰)"切分。
    /// SetValue / set_value / 写入 Set / SP1 命中;Offset / Reset / SPEED 不命中。
    /// </summary>
    private static bool HasWord (string name, string word)
    {
        var start = 0;

        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            var letterOrDigit = char.IsLetterOrDigit (ch);

            if (i == 0 || !letterOrDigit || (char.IsUpper (ch) && char.IsLower (name[i - 1])))
            {
                start = i;
            }

            if (!letterOrDigit) continue;

            // 只有当这个词正好到边界结束、长度又对得上时才算命中,
            // 否则 "SPEED" 会在前两个字母处被误判成 "SP"。
            var ends = i + 1 == name.Length
                || !char.IsLetterOrDigit (name[i + 1])
                || (char.IsUpper (name[i + 1]) && char.IsLower (ch));

            if (!ends || i - start + 1 != word.Length) continue;

            if (string.Compare (name, start, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 列出可下发命令的候选点。
    /// </summary>
    /// <param name="device">设备配置,可为 null。</param>
    /// <param name="isWritable">
    /// 单点是否可写的最终裁决(通常传 <see cref="CommandGate.IsWritable"/>)。
    /// 这一步会过滤掉只读寄存器、位地址、型号不支持的类型等协议级限制。
    /// </param>
    public static List<JogPoint> Select (DeviceConfig? device, Func<string, bool> isWritable)
    {
        var list = new List<JogPoint> ();

        if (device?.GroupList is null || isWritable is null) return list;

        foreach (var g in device.GroupList)
        {
            if (g.VarList is null) continue;
            if (!IsWritableGroup (g)) continue;

            foreach (var v in g.VarList)
            {
                if (string.IsNullOrWhiteSpace (v.VarName)) continue;
                if (!isWritable (v.VarName)) continue;

                list.Add (new JogPoint (
                    g.GroupName ?? "",
                    v.VarName,
                    (v.VarType ?? "").Trim(),
                    v.Comments ?? ""));
            }
        }

        return list;
    }

    /// <summary>急停目标 = 候选集里的全部 Bool 点。</summary>
    public static List<JogPoint> EStopTargets (IEnumerable<JogPoint> points) =>
        points.Where (p => p.IsBool).ToList ();
}