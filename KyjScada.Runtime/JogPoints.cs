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

        return name.Contains ("设定") || name.Contains ("参数")
            || name.Contains ("Set", StringComparison.OrdinalIgnoreCase)
            || name.Contains ("SP", StringComparison.Ordinal);
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