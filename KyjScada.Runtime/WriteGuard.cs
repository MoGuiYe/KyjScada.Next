namespace KyjScada.Runtime;

/// <summary>写入差异判定的结果。</summary>
/// <param name="NeedsConfirm">是否需要弹确认框。</param>
/// <param name="OutOfRange">是否越过了组态配的上下限(要更醒目的提示)。</param>
/// <param name="Reasons">给操作员看的逐条理由(空表示与当前状态一致)。</param>
public sealed record WriteCheck(bool NeedsConfirm, bool OutOfRange, IReadOnlyList<string> Reasons);

/// <summary>
/// 写入前的"这个值真的是你想写的吗"判定。
///
/// 抽成纯函数是因为这是**安全逻辑**:现场误写入绝大多数不是恶意,而是
/// "手滑多打一位"、"量纲填错"、"填到别人的量程"。判错的后果是设备跳变甚至执行机构动作,
/// 所以它必须能脱离界面单测,而不是埋在 MessageBox 前面的一段 if 里。
///
/// 判定口径:
///  1. 输入值与**当前实测值**不一致 → 要确认(最常见的手滑);
///  2. 超组态**高限/低限** → 要确认,并标成越界(量纲错、填错量程);
///  3. 组态没配范围时,偏离当前值超过 30% → 兜底确认(完全没有范围的点也得有人看守)。
///
/// 一致就静默放行:每写一次都弹框,操作员会养成"闭眼点确定"的习惯,提示反而失效。
/// </summary>
public static class WriteGuard
{
    /// <summary>没有组态范围时,允许的最大相对变化。</summary>
    public const double FallbackRelativeDelta = 0.30;

    /// <summary>浮点比较容差:配置里 1.6 和采样回来的 1.6 不能被判成"不同"。</summary>
    public const double Epsilon = 1e-6;

    public static WriteCheck Evaluate(
        double value,
        double? current,
        bool highEnabled,
        double high,
        bool lowEnabled,
        double low,
        string unit = "")
    {
        var reasons = new List<string>();
        var outOfRange = false;

        var suffix = string.IsNullOrWhiteSpace(unit) ? "" : " " + unit;

        if (current.HasValue && Math.Abs(current.Value - value) > Epsilon)
        {
            reasons.Add("当前值 " + Fmt(current.Value) + suffix + " → 将写入 " + Fmt(value) + suffix);
        }

        if (highEnabled && value > high)
        {
            reasons.Add("⚠ 超出组态高限 " + Fmt(high) + suffix);

            outOfRange = true;
        }

        if (lowEnabled && value < low)
        {
            reasons.Add("⚠ 低于组态低限 " + Fmt(low) + suffix);

            outOfRange = true;
        }

        // 组态两个限都没配时,才用粗兜底;配了范围就以配置为准,不再叠加噪音提示
        var hasRange = highEnabled || lowEnabled;

        if (!hasRange
            && current.HasValue
            && Math.Abs(current.Value) > Epsilon
            && Math.Abs(value - current.Value) > Math.Abs(current.Value) * FallbackRelativeDelta)
        {
            reasons.Add("⚠ 变化幅度超过当前值的 " + (int)(FallbackRelativeDelta * 100) + "%");
        }

        return new WriteCheck(reasons.Count > 0, outOfRange, reasons);
    }

    /// <summary>把判定结果拼成确认框正文。</summary>
    public static string Describe(string varName, string groupName, double value, double? current, WriteCheck check)
    {
        var text = "要写入的值与当前状态不一致,请确认:\n\n"
            + "变量: " + varName + (string.IsNullOrWhiteSpace(groupName) ? "" : "  (" + groupName + ")") + "\n"
            + "输入值: " + Fmt(value) + "\n"
            + (current.HasValue ? "当前值: " + Fmt(current.Value) : "当前值: 无(未采集到)")
            + "\n\n" + string.Join("\n", check.Reasons)
            + "\n\n确认要下发这个值吗?";

        return text;
    }

    private static string Fmt(double v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}