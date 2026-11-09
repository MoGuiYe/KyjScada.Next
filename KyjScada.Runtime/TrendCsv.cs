using System.Globalization;
using System.Text;

namespace KyjScada.Runtime;

/// <summary>导出一列曲线(纯数据,不带界面类型)。</summary>
public sealed record TrendCsvSeries(string VarName, string Unit, IReadOnlyList<TrendBucket> Points);

/// <summary>
/// 趋势导出 CSV 的拼行逻辑。
///
/// 关键设计:
///  1. **以第一条有数据的曲线的时间轴为基准**,其余曲线按时间就近取值填充。
///     直接按行号对齐是错的 —— 不同曲线的采样时刻和点数都可能不同。
///  2. **取不到就留空**,不补 0。补 0 会让人以为那一刻真是 0。
///  3. 全链路 InvariantCulture,换机器不会把小数点解析成逗号。
/// </summary>
public static class TrendCsv
{
    /// <summary>表头:时间,变量1/单位,变量2/单位...</summary>
    public static string BuildHeader(IReadOnlyList<TrendCsvSeries> series)
    {
        var sb = new StringBuilder();
        sb.Append("时间");

        foreach (var s in series)
        {
            sb.Append(',');

            // 先把"变量(单位)"拼成完整字段再转义。
            // 原来分两段各自 Escape:变量名里只要有逗号,就会变成 "a,b"(mm) ——
            // 引号提前收尾、单位落到引号外面,整行 CSV 的列数就错了,
            // Excel 打开后从这一列起全部错位。
            var label = s.VarName ?? "";

            if (!string.IsNullOrEmpty(s.Unit))
            {
                label += "(" + s.Unit + ")";
            }

            sb.Append(Escape(label));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 生成全部数据行。
    /// 时间轴取第一条有数据的曲线;其它曲线用最近点填充(超出容差留空)。
    /// </summary>
    public static List<string> BuildRows(IReadOnlyList<TrendCsvSeries> series)
    {
        var rows = new List<string>();

        if (series is null || series.Count == 0) return rows;

        // 基准时间轴:第一条有数据的曲线
        var baseIdx = -1;

        for (int i = 0; i < series.Count; i++)
        {
            if (series[i].Points is { Count: > 0 }) { baseIdx = i; break; }
        }

        if (baseIdx < 0) return rows;

        var basePts = series[baseIdx].Points;

        // 每条曲线各自的容差 = 2 × 中位间隔(至少 1 秒)
        var tolerances = new double[series.Count];

        for (int i = 0; i < series.Count; i++)
        {
            tolerances[i] = Math.Max(1, MedianIntervalOf(series[i].Points) * 2);
        }

        foreach (var bp in basePts)
        {
            if (bp.IsEmpty || double.IsNaN(bp.Value)) continue;

            var sb = new StringBuilder();

            sb.Append(bp.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

            for (int i = 0; i < series.Count; i++)
            {
                sb.Append(',');

                if (i == baseIdx)
                {
                    sb.Append(bp.Value.ToString("G9", CultureInfo.InvariantCulture));
                    continue;
                }

                var v = TryGetAt(series[i].Points, bp.Time, tolerances[i]);

                if (v.HasValue)
                {
                    sb.Append(v.Value.ToString("G9", CultureInfo.InvariantCulture));
                }
                // 取不到就什么都不写(留空),绝不补 0
            }

            rows.Add(sb.ToString());
        }

        return rows;
    }

    /// <summary>取时间上最近的点的值。超出容差返回 null。</summary>
    public static double? TryGetAt(IReadOnlyList<TrendBucket> pts, DateTime time, double toleranceSeconds)
    {
        if (pts is null || pts.Count == 0) return null;

        TrendBucket? best = null;
        var bestDiff = double.MaxValue;

        foreach (var p in pts)
        {
            if (p.IsEmpty) continue;
            if (double.IsNaN(p.Value) || double.IsInfinity(p.Value)) continue;

            var diff = Math.Abs((p.Time - time).TotalSeconds);

            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = p;
            }
        }

        if (best is null) return null;
        if (bestDiff > toleranceSeconds) return null;

        return best.Value;
    }

    private static double MedianIntervalOf(IReadOnlyList<TrendBucket> pts)
    {
        if (pts is null || pts.Count < 2) return 0;

        var n = Math.Min(pts.Count - 1, 1024);
        var stride = Math.Max(1, (pts.Count - 1) / n);

        var diffs = new List<double>(n);

        for (int i = stride; i < pts.Count && diffs.Count < n; i += stride)
        {
            // 抽样是隔 stride 个点取一对,差值要除以 stride 才是相邻点的间隔。
            // 不除的话容差 = 2 × 中位间隔 被放大 stride 倍,远点也会被当成"就近值"填进同一行,
            // 导出的表里不同时刻的值被拉平到同一条时间线上。
            var d = (pts[i].Time - pts[i - stride].Time).TotalSeconds / stride;
            if (d > 0) diffs.Add(d);
        }

        if (diffs.Count == 0) return 0;

        diffs.Sort();

        return diffs[diffs.Count / 2];
    }

    /// <summary>
    /// CSV 字段转义:含逗号/引号/换行时用双引号包起来,并挡掉公式注入。
    /// </summary>
    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        // 公式注入:变量名/单位来自组态(可由界面编辑,也可能来自导入的配置),
        // 以 = + - @ 开头时 Excel/WPS 会当公式执行,最坏能拉起 DDE/外部命令。
        // 前面加单引号,Excel 按文本显示,后端的 CSV 解析也能原样读回。
        //
        // 必须**先看第一个非空白字符**:Excel 会忽略前导空格再解析公式,
        // 于是 " =1+1" 照样被求值,而只看 s[0] 的写法放它直接过去。
        var first = 0;

        while (first < s.Length && (s[first] is ' ' or '\t')) first++;

        if (first < s.Length && s[first] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            s = "'" + s;
        }

        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;

        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}