namespace KyjScada.Runtime;

/// <summary>
/// 写入前的"这个值真的是你想写的吗"判定结果。
/// </summary>
/// <param name="NeedsConfirm">是否需要人工确认(否则静默放行)。</param>
/// <param name="OutOfRange">是否越过了组态给的**写入**上下限(比单纯"值变了"更严重)。</param>
/// <param name="Reasons">给操作员看的逐条理由(空表示没问题)。</param>
public sealed record WriteCheck(bool NeedsConfirm, bool OutOfRange, IReadOnlyList<string> Reasons)
{
    /// <summary>
    /// 无法核实(读不到当前值/质量不可信)。
    ///
    /// 与“越界”分开是因为处置不同:越界是**值本身危险**,哪都不能发;
    /// 不可核实是**没有依据**,本地可以问人,远程(MCP)必须直接拒 ——
    /// 没人看着的时候不能拿“不知道现状”当默认放行。
    /// </summary>
    public bool Unverifiable { get; init; }

    /// <summary>
    /// 远程(MCP)写入是否必须直接拒绝。
    ///
    /// 远程没人能点"确认",所以不能像本地那样问一句再发 —— 只能放行或拒绝。
    /// 判据:**值本身危险或无法核实就拒**;仅仅"与当前值不同"是正常操作,不能拒,
    /// 否则远程写就彻底没法用了(曾经把任何差异都拒掉,功能名存实亡)。
    /// </summary>
    public bool BlocksRemote => OutOfRange || Unverifiable;

    public static readonly WriteCheck Pass = new(false, false, Array.Empty<string>());
}

/// <summary>写入合法范围。null 表示该侧未配置。</summary>
public sealed record WriteLimits(double? High, double? Low, string Unit = "", bool IsBool = false);

/// <summary>当前采样值。Trusted=false 表示读不到/质量坏,不能拿来做比较。</summary>
public readonly record struct CurrentSample(double? Value, bool Trusted)
{
    public static readonly CurrentSample Unknown = new(null, false);

    public static CurrentSample Of(double value) => new(value, true);
}

/// <summary>
/// 写入前的值合法性判定。
///
/// 抽成纯函数是因为这是**安全逻辑**:现场误写入绝大多数不是恶意,而是
/// "手滑多打一位"、"量纲填错"、"填到别人的量程"。判错的后果是设备跳变甚至执行机构动作,
/// 所以它必须能脱离界面单测,也必须能被**所有**写入口共用(界面行、Bool 按钮、点动、
/// 急停、MCP 远程写),而不是每个入口各写一段 if。
///
/// 判定口径(按顺序):
///  1. Bool 点:只接受 0/1,别的值直接判不合法(这是 Bool 唯一的安全边界);
///  2. 当前值不可信(断线/未采集/质量坏):强制确认 —— 不知道现状就不能悄悄下发;
///  3. 与当前值不同:要确认(最常见的手滑);
///  4. 超出组态配置的**写入**上下限:要确认并标越界;
///  5. 没配写入范围时:偏离当前值超过 30% 也确认(完全没有范围的点也得有人看守)。
/// </summary>
public static class WriteGuard
{
    /// <summary>没有组态范围时,允许的最大相对变化。</summary>
    public const double FallbackRelativeDelta = 0.30;

    /// <summary>浮点比较容差:配置里 1.6 和采样回来的 1.6 不能被判成"不同"。</summary>
    public const double Epsilon = 1e-6;

    public static WriteCheck Evaluate(double value, CurrentSample current, WriteLimits? limits)
    {
        // NaN/Inf 属于"值本身没意义",本地不能写、远程更不能写
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return new WriteCheck(true, true, new[] { "值本身非法(NaN/Infinity)" }) { Unverifiable = true };
        }

        var isBool = limits?.IsBool == true;

        // Bool:只有 0/1 两个合法取值。0.5 这种值发下去设备行为不可预期
        if (isBool && Math.Abs(value) > Epsilon && Math.Abs(value - 1) > Epsilon)
        {
            return new WriteCheck(true, true, new[] { "Bool 点只接受 0(关)或 1(开),输入为 " + Fmt(value) });
        }

        var reasons = new List<string>();
        var outOfRange = false;
        var unverifiable = false;

        var unit = limits?.Unit is { Length: > 0 } u ? " " + u : "";

        // 当前值不可信:没有"与现状比较"的依据。
        // **这一条 Bool 也要查** —— 早先 Bool 分支直接 return Pass 跑在前面,
        // 断线时远程写启停线圈可以零校验直发,正是最危险的那一类。
        if (!current.Trusted || !current.Value.HasValue)
        {
            reasons.Add("⚠ 读不到当前值(未连接/未采集/质量不可信),无法判断这个值是否合理");

            unverifiable = true;
        }
        else if (!isBool && Math.Abs(current.Value.Value - value) > Epsilon)
        {
            // Bool 只豁免"差异确认":点动/启停本来就是改状态,问一句毫无意义。
            // 数值点才需要"你确定要从 1.6 改成 88 吗"。
            reasons.Add("当前值 " + Fmt(current.Value.Value) + unit + " → 将写入 " + Fmt(value) + unit);
        }

        // 写入上下限对 Bool 同样有效(配了就越界要拦),只是实际组态里很少给 Bool 配
        if (limits?.High is { } high && value > high)
        {
            reasons.Add("⚠ 超出写入上限 " + Fmt(high) + unit);

            outOfRange = true;
        }

        if (limits?.Low is { } low && value < low)
        {
            reasons.Add("⚠ 低于写入下限 " + Fmt(low) + unit);

            outOfRange = true;
        }

        // 组态一个限都没配时,才用粗兜底;配了范围就以配置为准,不再叠加噪音提示。
        // Bool 不参与:0↔1 天然是 100% 变化,套这个规则等于每次都弹框。
        var hasRange = limits?.High is not null || limits?.Low is not null;

        if (!isBool && !hasRange && current.Trusted && current.Value.HasValue
            && Math.Abs(current.Value.Value) > Epsilon
            && Math.Abs(value - current.Value.Value) > Math.Abs(current.Value.Value) * FallbackRelativeDelta)
        {
            reasons.Add("⚠ 变化幅度超过当前值的 " + (int)(FallbackRelativeDelta * 100) + "%");
        }

        return new WriteCheck(reasons.Count > 0, outOfRange, reasons) { Unverifiable = unverifiable };
    }

    /// <summary>把判定结果拼成确认框正文。</summary>
    public static string Describe(string varName, string groupName, double value, CurrentSample current, WriteCheck check)
    {
        var text = "要写入的值与当前状态不一致,请确认:\n\n"
            + "变量: " + varName + (string.IsNullOrWhiteSpace(groupName) ? "" : "  (" + groupName + ")") + "\n"
            + "输入值: " + Fmt(value) + "\n"
            + (current.Trusted && current.Value.HasValue
                ? "当前值: " + Fmt(current.Value.Value)
                : "当前值: 无(未采集到或质量不可信)")
            + "\n\n" + string.Join("\n", check.Reasons)
            + "\n\n确认要下发这个值吗?";

        return text;
    }

    private static string Fmt(double v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}