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
            sb.Append(Escape(s.VarName));

            if (!string.IsNullOrEmpty(s.Unit))
            {
                sb.Append('(');
                sb.Append(Escape(s.Unit));
                sb.Append(')');
            }
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
            var d = (pts[i].Time - pts[i - stride].Time).TotalSeconds;
            if (d > 0) diffs.Add(d);
        }

        if (diffs.Count == 0) return 0;

        diffs.Sort();

        return diffs[diffs.Count / 2];
    }

    /// <summary>CSV 字段转义:含逗号/引号/换行时用双引号包起来。</summary>
    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;

        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}