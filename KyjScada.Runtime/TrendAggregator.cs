using KyjScada.Domain.History;

namespace KyjScada.Runtime;

/// <summary>历史曲线的时间分辨率。</summary>
public enum TrendResolution
{
    /// <summary>原始值:不聚合,超限时只做等步长抽样。</summary>
    Raw = 0,

    /// <summary>自动:按跨度与目标点数推导。</summary>
    Auto = 1,

    S1 = 2,
    S10 = 3,
    S30 = 4,
    M1 = 5,
    M5 = 6,
    M30 = 7,
    H1 = 8,
    D1 = 9
}

/// <summary>分桶聚合函数。</summary>
public enum TrendAggregate
{
    Average = 0,
    Minimum = 1,
    Maximum = 2,
    First = 3,
    Last = 4,

    /// <summary>求和。累积量(电量/流量累计)必须用这个,不能求平均。</summary>
    Sum = 5
}

/// <summary>一个时间桶的聚合结果。空桶的 Value/Min/Max 均为 NaN。</summary>
public sealed record TrendBucket(DateTime Time, double Value, double Min, double Max, int Count)
{
    public bool IsEmpty => Count == 0;
}

/// <summary>
/// 历史趋势聚合器。把原始采样按时间桶聚合,同时算出每桶的最小/最大值(包络)。
///
/// 为什么必须做两层:
///  1. **点数控制** —— 7 天 × 10 秒间隔 = 6 万点,直接画会卡死且屏幕只有一两千像素。
///  2. **不能只取平均** —— 平均值会削平峰值。液位瞬间冲到 200 又掉回来,
///     平均下来可能只有 20,操作员照趋势图会得出"没超限"的错误结论。
///     所以每个桶必须同时保留 min/max,画成包络带。
///
/// 关键取舍:
///  - **空桶写 NaN 而不是补 0**。补 0 会让曲线掉到 0 再弹回,是"安全的假象"。
///  - **自动分辨率不伪造精度**。推导出的间隔不会小于原始采样的中位间隔,
///    否则会画出一条看起来比实际数据更细的曲线。
/// </summary>
public static class TrendAggregator
{
    /// <summary>目标点数。约等于屏幕宽度的 1~3 倍。</summary>
    public const int TargetPoints = 1500;

    /// <summary>原始模式下超过这个点数才抽样。</summary>
    public const int MaxRawPoints = 20000;

    /// <summary>把分辨率枚举换算成秒。Raw/Auto 返回 0(表示不固定间隔)。</summary>
    public static double IntervalSeconds(TrendResolution res)
    {
        switch (res)
        {
            case TrendResolution.S1: return 1;
            case TrendResolution.S10: return 10;
            case TrendResolution.S30: return 30;
            case TrendResolution.M1: return 60;
            case TrendResolution.M5: return 300;
            case TrendResolution.M30: return 1800;
            case TrendResolution.H1: return 3600;
            case TrendResolution.D1: return 86400;
            default: return 0;
        }
    }

    /// <summary>分辨率的中文名(界面下拉用)。</summary>
    public static string ResolutionName(TrendResolution res)
    {
        switch (res)
        {
            case TrendResolution.Raw: return "原始值";
            case TrendResolution.Auto: return "自动";
            case TrendResolution.S1: return "1 秒";
            case TrendResolution.S10: return "10 秒";
            case TrendResolution.S30: return "30 秒";
            case TrendResolution.M1: return "1 分钟";
            case TrendResolution.M5: return "5 分钟";
            case TrendResolution.M30: return "30 分钟";
            case TrendResolution.H1: return "1 小时";
            case TrendResolution.D1: return "1 天";
            default: return "自动";
        }
    }

    /// <summary>聚合函数的中文名。</summary>
    public static string AggregateName(TrendAggregate fn)
    {
        switch (fn)
        {
            case TrendAggregate.Minimum: return "最小值";
            case TrendAggregate.Maximum: return "最大值";
            case TrendAggregate.First: return "首值";
            case TrendAggregate.Last: return "末值";
            case TrendAggregate.Sum: return "求和";
            default: return "平均值";
        }
    }

    /// <summary>
    /// 推导自动分辨率(秒)。
    ///
    /// 规则:
    ///  - 间隔 ≈ 跨度 / 目标点数,再按采集中位间隔做下限保护(不伪造精度);
    ///  - 结果规整到工业上认的台阶(1/2/5/10/15/30 秒,1/2/5/10/15/30 分,1/2/3/6/12 时,1/2/7 天)。
    /// </summary>
    public static double ResolveAutoInterval(
        DateTime from, DateTime to, IReadOnlyList<HistoryRecord> raw, int targetPoints = TargetPoints)
    {
        var span = (to - from).TotalSeconds;
        if (span <= 0) span = 1;

        if (targetPoints < 10) targetPoints = 10;

        var ideal = span / targetPoints;

        // 下限保护:不能比原始采样间隔还细
        var median = MedianInterval(raw);
        if (median > 0 && ideal < median) ideal = median;

        return SnapToStep(ideal);
    }

    /// <summary>原始采样间隔的中位数(取最多 4096 个差值,避免大数组全排序)。</summary>
    public static double MedianInterval(IReadOnlyList<HistoryRecord> raw)
    {
        if (raw is null || raw.Count < 2) return 0;

        var maxDiffs = Math.Min(raw.Count - 1, 4096);
        var diffs = new double[maxDiffs];

        // 等距取样,避免长序列排序开销。
        // 注意:差值跨了 stride 个采样点,后面必须**除回 stride** —— 不除的话
        // 超过 4097 点(取样步长 >1)时中位间隔会被放大 stride 倍,
        // 自动粒度跟着变粗,趋势曲线把真实波动抹平。
        // (TrendStatsCalculator / TrendCsv 里同样的取样都除了,这里原先漏了)
        var stride = Math.Max(1, (raw.Count - 1) / maxDiffs);

        var n = 0;
        for (int i = stride; i < raw.Count && n < maxDiffs; i += stride)
        {
            diffs[n++] = (raw[i].SampleTime - raw[i - stride].SampleTime).TotalSeconds / stride;
        }

        if (n == 0) return 0;

        Array.Sort(diffs, 0, n);

        return diffs[n / 2];
    }

    /// <summary>把粗糙步长规整到工业台阶。小于 1 秒的按 1 秒算。</summary>
    public static double SnapToStep(double rough)
    {
        if (double.IsNaN(rough) || rough <= 0) return 1;

        double[] steps =
        {
            1, 2, 5, 10, 15, 30,
            60, 120, 300, 600, 900, 1800,
            3600, 7200, 10800, 21600, 43200,
            86400, 172800, 604800
        };

        foreach (var s in steps)
        {
            if (s >= rough) return s;
        }

        return steps[^1];
    }

    /// <summary>
    /// 按时间桶聚合。返回的桶按时间升序,空桶保留为 NaN 占位(曲线在此断开)。
    /// </summary>
    public static List<TrendBucket> Aggregate(
        IReadOnlyList<HistoryRecord> raw, DateTime from, DateTime to, double intervalSeconds, TrendAggregate fn)
    {
        return Aggregate (raw, from, to, intervalSeconds, fn, out _);
    }

    /// <summary>
    /// 同上,但把**实际生效**的间隔带出来。
    ///
    /// 桶数超上限时这里会把间隔放大到 span/MaxRawPoints;早先调用方拿到的仍是原值,
    /// 于是状态栏写着"1 秒/原始",图上实际约 30 秒一桶 —— 用户按 1 秒去读图必然读错。
    /// </summary>
    public static List<TrendBucket> Aggregate(
        IReadOnlyList<HistoryRecord> raw, DateTime from, DateTime to, double intervalSeconds, TrendAggregate fn,
        out double usedInterval)
    {
        var result = new List<TrendBucket>();

        usedInterval = intervalSeconds <= 0 ? 1 : intervalSeconds;

        if (raw is null || raw.Count == 0) return result;
        if (intervalSeconds <= 0) intervalSeconds = 1;

        var span = (to - from).TotalSeconds;
        if (span <= 0) span = 1;

        var bucketCount = (int)Math.Ceiling(span / intervalSeconds);

        // 防御:桶数上限,防止有人把间隔设成 0.1 秒时爆内存。
        // 但**不能只砍桶数**:砍了桶数却不放大间隔,超上限的点全被下面 idx>=bucketCount
        // 的钳位塞进最后一桶 —— 长区间会挤出一个包含几万点的巨型桶,
        // 图上看起来就是"最后一根柱子异常粗、前面全空"。
        // 放大间隔后,bucketCount 个桶仍然均匀覆盖整个区间,只是分辨率变粗。
        if (bucketCount > MaxRawPoints)
        {
            bucketCount = MaxRawPoints;
            intervalSeconds = span / bucketCount;
        }

        // 不论有没有放大,都把真正用的间隔回传:调用方显示的分辨率必须和画出来的桶一致
        usedInterval = intervalSeconds;

        // 每桶累积器
        var first = new double[bucketCount];
        var last = new double[bucketCount];
        var min = new double[bucketCount];
        var max = new double[bucketCount];
        var sum = new double[bucketCount];
        var count = new int[bucketCount];

        for (int i = 0; i < bucketCount; i++)
        {
            first[i] = double.NaN;
            last[i] = double.NaN;
            min[i] = double.NaN;
            max[i] = double.NaN;
        }

        foreach (var r in raw)
        {
            if (r.SampleTime < from || r.SampleTime > to) continue;

            // 坏值跳过(质量码 2 = Bad)
            if (r.Quality == HistoryQuality.Bad) continue;

            if (double.IsNaN(r.Value) || double.IsInfinity(r.Value)) continue;

            var idx = (int)((r.SampleTime - from).TotalSeconds / intervalSeconds);

            if (idx < 0) continue;

            // 查询区间是闭区间(前面已经用 <= to 放行),但 span 恰好是间隔整数倍时,
            // 落在 to 上的那个点算出来的 idx == bucketCount,会被这里静默丢掉。
            // 后果:区间跨度整好是 10s 的倍数时,曲线最右边那个最新值消失。
            // 归到最后一桶即可 —— 它本来就属于 [to - interval, to)。
            if (idx >= bucketCount) idx = bucketCount - 1;

            if (count[idx] == 0)
            {
                first[idx] = r.Value;
                min[idx] = r.Value;
                max[idx] = r.Value;
            }
            else
            {
                if (r.Value < min[idx]) min[idx] = r.Value;
                if (r.Value > max[idx]) max[idx] = r.Value;
            }

            last[idx] = r.Value;
            sum[idx] += r.Value;
            count[idx]++;
        }

        for (int i = 0; i < bucketCount; i++)
        {
            var t = from.AddSeconds(i * intervalSeconds);

            if (count[i] == 0)
            {
                // 空桶:NaN 占位,曲线在此断开(绝不补 0)
                result.Add(new TrendBucket(t, double.NaN, double.NaN, double.NaN, 0));
                continue;
            }

            double v;

            switch (fn)
            {
                case TrendAggregate.Minimum: v = min[i]; break;
                case TrendAggregate.Maximum: v = max[i]; break;
                case TrendAggregate.First: v = first[i]; break;
                case TrendAggregate.Last: v = last[i]; break;
                case TrendAggregate.Sum: v = sum[i]; break;
                default: v = sum[i] / count[i]; break;
            }

            result.Add(new TrendBucket(t, v, min[i], max[i], count[i]));
        }

        return result;
    }

    /// <summary>
    /// 原始模式抽稀。超过 MaxRawPoints 时等步长取样(**不插值**,保持真实采样点)。
    /// </summary>
    public static List<TrendBucket> ThinRaw(IReadOnlyList<HistoryRecord> raw, DateTime from, DateTime to)
    {
        var result = new List<TrendBucket>();

        if (raw is null || raw.Count == 0) return result;

        var step = raw.Count <= MaxRawPoints ? 1 : (int)Math.Ceiling(raw.Count / (double)MaxRawPoints);

        for (int i = 0; i < raw.Count; i += step)
        {
            var r = raw[i];

            if (r.SampleTime < from || r.SampleTime > to) continue;

            if (r.Quality == HistoryQuality.Bad) continue;

            if (double.IsNaN(r.Value) || double.IsInfinity(r.Value)) continue;

            // 原始模式没有桶,min=max=value
            result.Add(new TrendBucket(r.SampleTime, r.Value, r.Value, r.Value, 1));
        }

        return result;
    }

    /// <summary>
    /// 一条曲线的完整处理入口:按分辨率决定走原始抽稀还是分桶聚合。
    /// 返回的桶序列可直接喂给图表。
    ///
    /// 自动模式的关键优化:原始点数不超过目标点数时**直接走原始模式**。
    /// 否则 1 小时 / 11 秒采样的 327 个点会被聚合成 240 个桶,
    /// 白白丢掉精度还让时间戳对齐到桶边界。
    /// </summary>
    public static List<TrendBucket> Process(
        IReadOnlyList<HistoryRecord> raw, DateTime from, DateTime to,
        TrendResolution resolution, TrendAggregate fn, out double usedInterval)
    {
        if (resolution == TrendResolution.Raw)
        {
            usedInterval = 0;
            return ThinRaw(raw, from, to);
        }

        if (resolution == TrendResolution.Auto)
        {
            // 点数本来就不多:不聚合,保留真实采样点和时间戳
            if (raw is not null && raw.Count <= TargetPoints)
            {
                usedInterval = 0;
                return ThinRaw(raw, from, to);
            }

            usedInterval = ResolveAutoInterval(from, to, raw);
        }
        else
        {
            usedInterval = IntervalSeconds(resolution);
        }

        if (usedInterval <= 0) usedInterval = 1;

        // Aggregate 可能在桶数超上限时把间隔放大,必须让它回传实际值,
        // 否则状态栏显示的分辨率会和图上实际的桶宽对不上。
        return Aggregate(raw, from, to, usedInterval, fn, out usedInterval);
    }
}