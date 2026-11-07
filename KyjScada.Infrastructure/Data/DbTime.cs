using System.Globalization;

namespace KyjScada.Infrastructure.Data;

/// <summary>
/// 库里的时间一律以**本地时间文本**存储,查询条件也用字符串比较。
/// 这里统一"DateTime → 库里那个字符串"的换算,避免各处各写一遍。
///
/// 为什么必须有这一层:
///  1. 调用方给的时间 Kind 五花八门 —— 界面控件给的是 Unspecified、算出来的是 Local、
///     从 AI 客户端解析出来的可能是 Utc。`ToString("o")` 对 Utc 会带 "Z" 后缀,
///     而库里的行是 "+08:00"。文本比较里 'Z'(0x5A) 恒大于 '+'(0x2B),
///     结果就是"UTC 时间查询什么也查不到"。
///  2. 反过来,Unspecified 的 "o" 输出**没有**偏移后缀。它恰好是带后缀文本的前缀,
///     字符串比较时"同一毫秒的那一行"会被排除在区间外 —— 边界上的数据静默消失。
///
/// 所以:先换算成本地时间,再按固定格式格式化,保证写库与查询用的是同一种字符串。
/// </summary>
public static class DbTime
{
    /// <summary>把任意 Kind 的时间换算成"本地墙上时间"的 DateTime(不带偏移信息)。</summary>
    public static DateTime ToLocal(DateTime time) =>
        time.Kind switch
        {
            DateTimeKind.Utc => time.ToLocalTime(),
            DateTimeKind.Local => time,
            _ => DateTime.SpecifyKind(time, DateTimeKind.Local)
        };

    /// <summary>按指定格式生成本地时间文本(如历史库的 "yyyy-MM-dd HH:mm:ss")。</summary>
    public static string Format(DateTime time, string format) =>
        ToLocal(time).ToString(format, CultureInfo.InvariantCulture);

    /// <summary>
    /// 往返格式("o",带小数秒与 +08:00 偏移)。报警库 OccurredAt/AckAt 用它 ——
    /// 报警要精确到毫秒,而历史库只存到秒。
    /// </summary>
    public static string FormatRoundtrip(DateTime time) =>
        Format(time, "o");
}