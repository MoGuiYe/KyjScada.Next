using KyjScada.Domain.History;

namespace KyjScada.Runtime;

/// <summary>一段区间的统计结果。</summary>
public sealed record TrendStats(
    int ValidCount,
    double Min,
    double Max,
    double Average,
    double StdDev,
    double First,
    double Last,
    double Delta,
    double Integral,
    double OverLimitSeconds,
    double InLimitSeconds,
    int SwitchCount,
    bool HasData,
    /// <summary>
    /// 实际被数据覆盖的时长(相邻有效点的时间差之和)。
    /// 与框选跨度之差就是**没有数据的空洞** —— 必须让人看得见,
    /// 否则"合格 0 秒"会让人误以为整段都在超限,其实大部分时间没采到数据。
    /// </summary>
    double CoveredSeconds = 0)
{
    public static readonly TrendStats Empty = new(0, double.NaN, double.NaN, double.NaN, double.NaN,
        double.NaN, double.NaN, double.NaN, double.NaN, 0, 0, 0, false, 0);
}

/// <summary>一段数据缺失(通讯中断/停机造成的空档)。</summary>
public sealed record TrendGap(DateTime From, DateTime To)
{
    public double Seconds => (To - From).TotalSeconds;
}

/// <summary>
/// 区间统计计算。
///
/// 三个容易算错的地方(老项目踩过):
///  1. **标准差必须用 Welford 在线算法**。用 `sum(v²)/n - avg²` 在"大偏置 + 小波动"
///     的信号上会发生灾难性抵消 —— 比如 1000±0.1 的温度,方差算出来恒为 0。
///  2. **超限时长按两端比例折算**。只看前一点会漏掉"后一点刚超限"的那一段。
///  3. **合格时长的分母用真实被数据覆盖的时长**,不是查询区间总长。
///     否则通讯中断留下的空洞会被算成"合格",把可用率虚高。
/// </summary>
public static class TrendStatsCalculator
{
    /// <summary>
    /// 找数据缺失区间。
    ///
    /// 判定:相邻有效点间隔超过 minGapSeconds 就算一处缺失。
    /// 这个阈值不能太小 —— 采样间隔本身有抖动,阈值等于采集周期会把每两个点都判成缺失。
    ///
    /// 为什么要单独列出来:只显示"缺失总时长"看不出到底是"一次断了 20 分钟"
    /// 还是"断了 20 次每次 1 分钟",这两种情况的处理方式完全不同。
    /// </summary>
    public static List<TrendGap> FindGaps(
        IReadOnlyList<TrendBucket> points, DateTime from, DateTime to, double minGapSeconds)
    {
        var gaps = new List<TrendGap>();

        if (points is null || points.Count == 0) return gaps;
        if (minGapSeconds <= 0) minGapSeconds = 60;

        TrendBucket? prev = null;

        foreach (var p in points)
        {
            if (p.Time < from) continue;
            if (p.Time > to) break;

            if (p.IsEmpty || double.IsNaN(p.Value) || double.IsInfinity(p.Value)) continue;

            if (prev is not null)
            {
                var dt = (p.Time - prev.Time).TotalSeconds;

                if (dt > minGapSeconds)
                {
                    // 空档从上一个点结束算起到下一个点开始
                    gaps.Add(new TrendGap(prev.Time, p.Time));
                }
            }

            prev = p;
        }

        return gaps;
    }

    /// <summary>按曲线采样间隔推算"算缺失"的阈值:2 倍中位间隔,至少 60 秒。</summary>
    public static double GapThreshold(IReadOnlyList<TrendBucket> points)
    {
        if (points is null || points.Count < 2) return 60;

        var n = Math.Min(points.Count - 1, 512);
        var stride = Math.Max(1, (points.Count - 1) / n);

        var diffs = new List<double>(n);

        for (int i = stride; i < points.Count && diffs.Count < n; i += stride)
        {
            var d = (points[i].Time - points[i - stride].Time).TotalSeconds;
            if (d > 0) diffs.Add(d);
        }

        if (diffs.Count == 0) return 60;

        diffs.Sort();

        return Math.Max(60, diffs[diffs.Count / 2] * 2);
    }

    /// <summary>
    /// 计算一段区间的统计。
    /// </summary>
    /// <param name="points">已按时间升序的桶序列。</param>
    /// <param name="from">区间起点(含)。</param>
    /// <param name="to">区间终点(含)。</param>
    /// <param name="highLimit">上限,null 或 0 表示未配置。</param>
    /// <param name="lowLimit">下限,null 或 0 表示未配置。</param>
    public static TrendStats Compute(
        IReadOnlyList<TrendBucket> points, DateTime from, DateTime to,
        double? highLimit, double? lowLimit)
    {
        if (points is null || points.Count == 0) return TrendStats.Empty;

        // 只取区间内且有效的桶
        var valid = new List<TrendBucket>();

        foreach (var p in points)
        {
            if (p.Time < from) continue;
            if (p.Time > to) break;

            if (p.IsEmpty) continue;
            if (double.IsNaN(p.Value) || double.IsInfinity(p.Value)) continue;

            valid.Add(p);
        }

        if (valid.Count == 0) return TrendStats.Empty;

        // ---- 基本统计量 + Welford 在线方差 ----
        var min = double.MaxValue;
        var max = double.MinValue;
        var sum = 0.0;

        var wMean = 0.0;
        var wM2 = 0.0;
        var n = 0;

        foreach (var p in valid)
        {
            if (p.Value < min) min = p.Value;
            if (p.Value > max) max = p.Value;

            sum += p.Value;

            n++;
            var delta = p.Value - wMean;
            wMean += delta / n;
            wM2 += delta * (p.Value - wMean);
        }

        var avg = sum / valid.Count;
        var stdDev = n > 1 ? Math.Sqrt(wM2 / (n - 1)) : 0;

        // ---- 时序量:积分 / 超限时长 / 切换次数 ----
        var integral = 0.0;
        var covered = 0.0;
        var over = 0.0;
        var switches = 0;

        for (int i = 1; i < valid.Count; i++)
        {
            var dt = (valid[i].Time - valid[i - 1].Time).TotalSeconds;
            if (dt <= 0) continue;

            // 梯形积分:累积量(如电量)用它
            integral += (valid[i - 1].Value + valid[i].Value) / 2 * dt;

            covered += dt;

            var aOver = IsOver(valid[i - 1].Value, highLimit, lowLimit);
            var bOver = IsOver(valid[i].Value, highLimit, lowLimit);

            // 两端都超 = 整段超;只有一端超 = 半段超(线性插值折算)
            var ratio = aOver && bOver ? 1.0 : (aOver || bOver ? 0.5 : 0.0);
            over += dt * ratio;

            // 切换次数:相邻两点的超限状态不同即算一次
            if (aOver != bOver) switches++;
        }

        var first = valid[0].Value;
        var last = valid[^1].Value;

        return new TrendStats(
            ValidCount: valid.Count,
            Min: min,
            Max: max,
            Average: avg,
            StdDev: stdDev,
            First: first,
            Last: last,
            Delta: last - first,
            Integral: integral,
            OverLimitSeconds: over,
            InLimitSeconds: Math.Max(0, covered - over),
            SwitchCount: switches,
            HasData: true,
            CoveredSeconds: covered);
    }

    private static bool IsOver(double v, double? high, double? low)
    {
        if (high.HasValue && high.Value != 0 && v > high.Value) return true;
        if (low.HasValue && low.Value != 0 && v < low.Value) return true;

        return false;
    }

    /// <summary>把秒数格式化成 "1小时23分45秒" 这种可读文本。</summary>
    public static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) return "--";

        var ts = TimeSpan.FromSeconds(seconds);

        if (ts.TotalHours >= 1)
            return ((int)ts.TotalHours) + "小时" + ts.Minutes + "分" + ts.Seconds + "秒";

        if (ts.TotalMinutes >= 1)
            return ts.Minutes + "分" + ts.Seconds + "秒";

        return ((int)ts.TotalSeconds) + "秒";
    }
}