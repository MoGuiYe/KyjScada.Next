using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 区间统计 + CSV 导出测试。
///
/// 重点验证:
///  1. **标准差用 Welford**:大偏置小波动信号(1000±0.1)用朴素公式会算成 0;
///  2. 超限时长按两端比例折算;
///  3. CSV 以第一条曲线时间轴对齐,取不到留空**而不是补 0**。
/// </summary>
public static class TrendStatsTests
{
    public static void RunAll(Action<string, Action> run)
    {
        RunBasic(run);
        RunStdDev(run);
        RunIntegral(run);
        RunLimitDuration(run);
        RunFormatDuration(run);
        RunCsv(run);
    }

    private static TrendBucket B(DateTime t, double v, double? min = null, double? max = null)
        => new(t, v, min ?? v, max ?? v, 1);

    private static List<TrendBucket> Series(DateTime start, int count, double seconds, Func<int, double> f)
    {
        var list = new List<TrendBucket>(count);

        for (int i = 0; i < count; i++)
        {
            list.Add(B(start.AddSeconds(i * seconds), f(i)));
        }

        return list;
    }

    private static void RunBasic(Action<string, Action> run)
    {
        run("区间统计:基本量(最小/最大/平均/首/末/变化量)", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 10, 0, 0);
            var pts = Series(t0, 5, 10, i => new[] { 5.0, 3.0, 9.0, 1.0, 7.0 }[i]);

            var st = TrendStatsCalculator.Compute(pts, t0, t0.AddMinutes(1), null, null);

            TestHelpers.AssertTrue(st.HasData, "应有数据");
            TestHelpers.AssertEq(5, st.ValidCount, "有效点数应为 5");
            TestHelpers.AssertTrue(Math.Abs(st.Min - 1) < 1e-9, "最小应为 1");
            TestHelpers.AssertTrue(Math.Abs(st.Max - 9) < 1e-9, "最大应为 9");
            TestHelpers.AssertTrue(Math.Abs(st.Average - 5) < 1e-9, "平均应为 (5+3+9+1+7)/5=5");
            TestHelpers.AssertTrue(Math.Abs(st.First - 5) < 1e-9, "首值应为 5");
            TestHelpers.AssertTrue(Math.Abs(st.Last - 7) < 1e-9, "末值应为 7");
            TestHelpers.AssertTrue(Math.Abs(st.Delta - 2) < 1e-9, "变化量应为 7-5=2");
        });

        run("区间统计:空数据返回 Empty", () =>
        {
            var st = TrendStatsCalculator.Compute(
                new List<TrendBucket>(), DateTime.Now, DateTime.Now.AddHours(1), null, null);

            TestHelpers.AssertTrue(!st.HasData, "空数据 HasData 应为 false");
        });

        run("区间统计:区间外和空桶被排除", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 11, 0, 0);

            var pts = new List<TrendBucket>
            {
                B(t0.AddSeconds(-60), 999),            // 区间外
                B(t0, 10),
                new(t0.AddSeconds(10), double.NaN, double.NaN, double.NaN, 0),  // 空桶
                B(t0.AddSeconds(20), 20),
                B(t0.AddSeconds(300), 888)            // 区间外
            };

            var st = TrendStatsCalculator.Compute(pts, t0, t0.AddSeconds(60), null, null);

            TestHelpers.AssertEq(2, st.ValidCount, "只应有 2 个有效点");
            TestHelpers.AssertTrue(Math.Abs(st.Max - 20) < 1e-9, "最大应为 20,不能把区间外的 999 算进来");
        });
    }

    private static void RunStdDev(Action<string, Action> run)
    {
        run("区间统计:标准差用 Welford,大偏置小波动不塌陷", () =>
        {
            // 1000 ± 0.1 —— 朴素公式 sumSq/n - avg² 在这里会灾难性抵消,σ 算成 0
            var t0 = new DateTime(2026, 11, 4, 12, 0, 0);

            var pts = Series(t0, 100, 10, i => 1000.0 + (i % 2 == 0 ? 0.1 : -0.1));

            var st = TrendStatsCalculator.Compute(pts, t0, t0.AddMinutes(20), null, null);

            // 理论样本标准差 = 0.1005(±0.1 交替,n=100)
            TestHelpers.AssertTrue(st.StdDev > 0.09 && st.StdDev < 0.11,
                "标准差应约 0.1005,实际 " + st.StdDev);
        });

        run("区间统计:常量序列标准差为 0", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 13, 0, 0);
            var pts = Series(t0, 50, 10, i => 42.0);

            var st = TrendStatsCalculator.Compute(pts, t0, t0.AddMinutes(10), null, null);

            TestHelpers.AssertTrue(st.StdDev < 1e-9, "常量序列标准差应为 0,实际 " + st.StdDev);
        });
    }

    private static void RunIntegral(Action<string, Action> run)
    {
        run("区间统计:梯形积分(常量 10 × 60 秒 = 600)", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 14, 0, 0);

            // 7 个点,间隔 10 秒,值恒为 10 → 覆盖 60 秒
            var pts = Series(t0, 7, 10, i => 10.0);

            var st = TrendStatsCalculator.Compute(pts, t0, t0.AddMinutes(2), null, null);

            TestHelpers.AssertTrue(Math.Abs(st.Integral - 600) < 1e-6,
                "积分应为 10×60=600,实际 " + st.Integral);
        });

        run("区间统计:三角波积分(0→10→0,底 20 秒高 10 = 100)", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 15, 0, 0);

            var pts = new List<TrendBucket>
            {
                B(t0, 0),
                B(t0.AddSeconds(10), 10),
                B(t0.AddSeconds(20), 0)
            };

            var st = TrendStatsCalculator.Compute(pts, t0, t0.AddSeconds(20), null, null);

            TestHelpers.AssertTrue(Math.Abs(st.Integral - 100) < 1e-6,
                "梯形积分应为 100,实际 " + st.Integral);
        });
    }

    private static void RunLimitDuration(Action<string, Action> run)
    {
        run("区间统计:全程超限", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 16, 0, 0);
            var pts = Series(t0, 7, 10, i => 200.0);   // 覆盖 60 秒

            var st = TrendStatsCalculator.Compute(pts, t0, t0.AddMinutes(2), 100, null);

            TestHelpers.AssertTrue(Math.Abs(st.OverLimitSeconds - 60) < 1e-6,
                "应全程超限 60 秒,实际 " + st.OverLimitSeconds);
            TestHelpers.AssertTrue(Math.Abs(st.InLimitSeconds) < 1e-6, "合格时长应为 0");
        });

        run("区间统计:全程合格", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 17, 0, 0);
            var pts = Series(t0, 7, 10, i => 50.0);

            var st = TrendStatsCalculator.Compute(pts, t0, t0.AddMinutes(2), 100, null);

            TestHelpers.AssertTrue(Math.Abs(st.OverLimitSeconds) < 1e-6, "超限时长应为 0");
            TestHelpers.AssertTrue(Math.Abs(st.InLimitSeconds - 60) < 1e-6,
                "合格应为 60 秒,实际 " + st.InLimitSeconds);
        });

        run("区间统计:一端超限按半段折算", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 18, 0, 0);

            // 50 → 200:前点合格、后点超限,这 10 秒应算一半
            var pts = new List<TrendBucket>
            {
                B(t0, 50),
                B(t0.AddSeconds(10), 200)
            };

            var st = TrendStatsCalculator.Compute(pts, t0, t0.AddSeconds(10), 100, null);

            TestHelpers.AssertTrue(Math.Abs(st.OverLimitSeconds - 5) < 1e-6,
                "一端超限应折半为 5 秒,实际 " + st.OverLimitSeconds);
        });

        run("区间统计:切换次数", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 19, 0, 0);

            // 50(合格) → 200(超) → 50(合格) → 200(超)
            var pts = new List<TrendBucket>
            {
                B(t0, 50),
                B(t0.AddSeconds(10), 200),
                B(t0.AddSeconds(20), 50),
                B(t0.AddSeconds(30), 200)
            };

            var st = TrendStatsCalculator.Compute(pts, t0, t0.AddSeconds(30), 100, null);

            // 状态序列:正常→超→正常→超 = 3 次切换
            TestHelpers.AssertEq(3, st.SwitchCount, "应有 3 次切换");
        });

        run("区间统计:下限超限", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 20, 0, 0);
            var pts = Series(t0, 7, 10, i => 5.0);

            var st = TrendStatsCalculator.Compute(pts, t0, t0.AddMinutes(2), null, 10);

            TestHelpers.AssertTrue(Math.Abs(st.OverLimitSeconds - 60) < 1e-6,
                "低于下限应算超限,实际 " + st.OverLimitSeconds);
        });

        run("区间统计:覆盖时长 = 相邻有效点时间差之和", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 21, 0, 0);

            // 7 个点,间隔 10 秒 → 覆盖 60 秒
            var pts = Series(t0, 7, 10, i => 50.0);

            var st = TrendStatsCalculator.Compute(pts, t0, t0.AddMinutes(5), 100, null);

            TestHelpers.AssertTrue(Math.Abs(st.CoveredSeconds - 60) < 1e-6,
                "覆盖应为 60 秒,实际 " + st.CoveredSeconds);
            TestHelpers.AssertTrue(Math.Abs(st.OverLimitSeconds) < 1e-6,
                "值 50 低于上限 100,超限应为 0");
            TestHelpers.AssertTrue(Math.Abs(st.InLimitSeconds - 60) < 1e-6,
                "合格应为 60 秒");
        });

        run("区间统计:数据有空洞时覆盖时长小于框选跨度", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 22, 0, 0);

            // 前 3 点密排(10 秒),然后空 5 分钟,再来 2 点
            var pts = new List<TrendBucket>
            {
                B(t0, 10),
                B(t0.AddSeconds(10), 11),
                B(t0.AddSeconds(20), 12),
                B(t0.AddSeconds(320), 13),
                B(t0.AddSeconds(330), 14)
            };

            var st = TrendStatsCalculator.Compute(pts, t0, t0.AddMinutes(6), null, null);

            // 覆盖 = 10+10+300+10 = 330 秒,而框选跨度 360 秒
            TestHelpers.AssertTrue(Math.Abs(st.CoveredSeconds - 330) < 1e-6,
                "覆盖应为 330 秒,实际 " + st.CoveredSeconds);
            TestHelpers.AssertTrue(st.CoveredSeconds < 360, "覆盖应小于跨度");
        });

        run("缺失区间:识别连续空档", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 23, 0, 0);

            var pts = new List<TrendBucket>
            {
                B(t0, 1),
                B(t0.AddSeconds(10), 2),
                B(t0.AddSeconds(300), 3),   // 断了 290 秒
                B(t0.AddSeconds(310), 4),
                B(t0.AddSeconds(700), 5)    // 又断了 390 秒
            };

            var gaps = TrendStatsCalculator.FindGaps (pts, t0, t0.AddSeconds(700), 60);

            TestHelpers.AssertEq(2, gaps.Count, "应识别出 2 处缺失");
            TestHelpers.AssertTrue(Math.Abs(gaps[0].Seconds - 290) < 1e-6,
                "第一处应为 290 秒,实际 " + gaps[0].Seconds);
            TestHelpers.AssertTrue(Math.Abs(gaps[1].Seconds - 390) < 1e-6,
                "第二处应为 390 秒,实际 " + gaps[1].Seconds);
        });

        run("缺失区间:正常采样间隔不算缺失", () =>
        {
            var t0 = new DateTime(2026, 11, 5, 0, 0, 0);
            var pts = Series(t0, 10, 10, i => i);   // 每 10 秒一个点

            var gaps = TrendStatsCalculator.FindGaps (pts, t0, t0.AddMinutes(5), 60);

            TestHelpers.AssertEq(0, gaps.Count, "10 秒间隔、阈值 60 秒,不应有缺失");
        });

        run("缺失区间:阈值按中位间隔的 2 倍", () =>
        {
            var t0 = new DateTime(2026, 11, 5, 1, 0, 0);
            var pts = Series(t0, 20, 30, i => i);   // 30 秒间隔

            var threshold = TrendStatsCalculator.GapThreshold (pts);

            TestHelpers.AssertTrue(Math.Abs(threshold - 60) < 1e-6,
                "30 秒间隔的阈值应为 60 秒,实际 " + threshold);
        });

        run("缺失区间:点数不足时阈值为 60 秒", () =>
        {
            TestHelpers.AssertTrue(Math.Abs(TrendStatsCalculator.GapThreshold (new List<TrendBucket>()) - 60) < 1e-6,
                "空列表应为 60");
        });
    }

    private static void RunFormatDuration(Action<string, Action> run)
    {
        run("区间统计:时长格式化", () =>
        {
            TestHelpers.AssertStr("30秒", TrendStatsCalculator.FormatDuration(30), "30 秒");
            TestHelpers.AssertStr("2分5秒", TrendStatsCalculator.FormatDuration(125), "125 秒");
            TestHelpers.AssertStr("1小时1分1秒", TrendStatsCalculator.FormatDuration(3661), "3661 秒");
            TestHelpers.AssertStr("--", TrendStatsCalculator.FormatDuration(double.NaN), "NaN 应显示 --");
        });
    }

    private static void RunCsv(Action<string, Action> run)
    {
        run("CSV:表头含变量与单位", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 10, 0, 0);

            var series = new List<TrendCsvSeries>
            {
                new("LQT_Level", "m", Series(t0, 2, 10, i => 1 + i)),
                new("LQT_InTemp", "C", Series(t0, 2, 10, i => 10 + i))
            };

            var header = TrendCsv.BuildHeader(series);

            TestHelpers.AssertStr("时间,LQT_Level(m),LQT_InTemp(C)", header, "表头格式错误");
        });

        run("CSV:变量名含逗号时加引号转义", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 10, 0, 0);

            var series = new List<TrendCsvSeries>
            {
                new("A,B", "", Series(t0, 1, 10, i => 1))
            };

            var header = TrendCsv.BuildHeader(series);

            TestHelpers.AssertTrue(header.Contains("\"A,B\""), "含逗号应加引号,实际 " + header);
        });

        run("CSV:数据行按第一条曲线时间轴对齐", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 11, 0, 0);

            // 曲线 A 每 10 秒一个点(3 个),曲线 B 每 20 秒一个点(2 个)
            var a = Series(t0, 3, 10, i => 100 + i);
            var b = Series(t0, 2, 20, i => 200 + i);

            var series = new List<TrendCsvSeries>
            {
                new("A", "", a),
                new("B", "", b)
            };

            var rows = TrendCsv.BuildRows(series);

            TestHelpers.AssertEq(3, rows.Count, "应以曲线 A 的时间轴为准,3 行");

            // 第 2 行(t0+10)曲线 B 没有采样点(容差 2×20=40 秒内会取最近点 t0 或 t0+20)
            // 容差内取值是允许的,这里只验证行数对齐
            TestHelpers.AssertTrue(rows[0].StartsWith("2026-11-04 11:00:00,"), "首列应是时间");
        });

        run("CSV:取不到的值留空而不是补 0", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 12, 0, 0);

            var a = Series(t0, 3, 10, i => 1 + i);

            // B 只有一个很早的点,距离后续时刻远超容差
            var b = new List<TrendBucket> { B(t0, 999) };

            var series = new List<TrendCsvSeries>
            {
                new("A", "", a),
                new("B", "", b)
            };

            var rows = TrendCsv.BuildRows(series);

            // 最后一行:t0+20,距 B 的点 20 秒,容差 = max(1, 2×中位间隔)
            // 单点没有中位间隔 → 容差 1 秒 → 取不到 → 留空
            var last = rows[^1];

            TestHelpers.AssertTrue(last.EndsWith(","), "取不到应留空(行尾逗号),实际 " + last);
        });

        run("CSV:数值用 InvariantCulture(小数点是句点)", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 13, 0, 0);

            var a = Series(t0, 2, 10, i => 1.5 + i);

            var series = new List<TrendCsvSeries> { new("A", "", a) };
            var rows = TrendCsv.BuildRows(series);

            TestHelpers.AssertTrue(rows[0].EndsWith(",1.5"), "应为 1.5 不是 1,5,实际 " + rows[0]);
        });

        run("CSV:空曲线集返回空行", () =>
        {
            var rows = TrendCsv.BuildRows(new List<TrendCsvSeries>());

            TestHelpers.AssertEq(0, rows.Count, "空集应无数据行");
        });

        run("CSV:TryGetAt 容差判空", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 14, 0, 0);
            var pts = new List<TrendBucket> { B(t0, 42) };

            var hit = TrendCsv.TryGetAt(pts, t0.AddSeconds(0.5), 1);
            TestHelpers.AssertTrue(hit.HasValue && Math.Abs(hit.Value - 42) < 1e-9, "容差内应取到 42");

            var miss = TrendCsv.TryGetAt(pts, t0.AddSeconds(10), 1);
            TestHelpers.AssertTrue(!miss.HasValue, "超出容差应返回 null");
        });
    }
}