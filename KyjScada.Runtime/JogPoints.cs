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
    /// 名称里是否含有独立的词:按"非字母数字"、"小写→大写(驼峰)"、"字母↔数字"切分。
    /// SetValue / set_value / 写入 Set / SP1 命中;Offset / Reset / SPEED 不命中。
    /// </summary>
    private static bool HasWord (string name, string word)
    {
        // 词的起点,-1 表示"当前不在词里"。
        // 早先是 start=i 直接停在分隔符下标上,于是 "写入 Set" 里的 Set 被算成 4 个字符,
        // 长度对不上、永远不命中 —— 而这句判断决定"哪些点允许被写入",漏判就是设定值写不进去。
        var start = -1;

        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];

            if (!char.IsLetterOrDigit (ch))
            {
                // 分隔符:收掉前面那个词,然后从下一个字符重新开始
                if (start >= 0 && TokenMatches (name, start, i, word)) return true;

                start = -1;

                continue;
            }

            if (start < 0)
            {
                start = i;

                continue;
            }

            var prev = name[i - 1];

            // 驼峰:小写→大写 是新词开头(SetValue → Set / Value)
            if (char.IsUpper (ch) && char.IsLower (prev))
            {
                if (TokenMatches (name, start, i, word)) return true;

                start = i;

                continue;
            }

            // 字母↔数字 也是边界:SP1 / Set2 里的 SP / Set 要能命中,
            // 否则下位机常见的 "SP1" 这种命名会整体漏判
            if (char.IsDigit (ch) != char.IsDigit (prev))
            {
                if (TokenMatches (name, start, i, word)) return true;

                start = char.IsLetter (ch) ? i : -1;
            }
        }

        return start >= 0 && TokenMatches (name, start, name.Length, word);
    }

    /// <summary>比较 [start, end) 这段字符是否正好是 word(忽略大小写)。</summary>
    private static bool TokenMatches (string name, int start, int end, string word)
    {
        return end - start == word.Length
            && string.Compare (name, start, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0;
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