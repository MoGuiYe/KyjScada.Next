using KyjScada.Domain.History;
using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 趋势聚合器测试。
///
/// 重点验证三件事:
///  1. 6 种聚合函数结果正确;
///  2. **空桶写 NaN 而不是 0**(补 0 会造成"安全的假象");
///  3. 自动分辨率**不伪造精度**(不比原始采样间隔更细)。
/// </summary>
public static class TrendAggregatorTests
{
    public static void RunAll(Action<string, Action> run)
    {
        RunSnap(run);
        RunMedian(run);
        RunAutoInterval(run);
        RunAggregate(run);
        RunEmptyBucket(run);
        RunEnvelope(run);
        RunThinRaw(run);
    }

    private static HistoryRecord Rec(DateTime t, double v, int q = HistoryQuality.Good)
        => new(t, 1, v, q);

    /// <summary>造一段等间隔数据。</summary>
    private static List<HistoryRecord> Series(DateTime start, int count, double seconds, Func<int, double> value)
    {
        var list = new List<HistoryRecord>(count);

        for (int i = 0; i < count; i++)
        {
            list.Add(Rec(start.AddSeconds(i * seconds), value(i)));
        }

        return list;
    }

    private static void RunSnap(Action<string, Action> run)
    {
        run("聚合:步长规整到工业台阶", () =>
        {
            TestHelpers.AssertTrue(Math.Abs(TrendAggregator.SnapToStep(0.3) - 1) < 1e-9, "0.3 → 1");
            TestHelpers.AssertTrue(Math.Abs(TrendAggregator.SnapToStep(3) - 5) < 1e-9, "3 → 5");
            TestHelpers.AssertTrue(Math.Abs(TrendAggregator.SnapToStep(7) - 10) < 1e-9, "7 → 10");
            TestHelpers.AssertTrue(Math.Abs(TrendAggregator.SnapToStep(45) - 60) < 1e-9, "45 → 60");
            TestHelpers.AssertTrue(Math.Abs(TrendAggregator.SnapToStep(4000) - 7200) < 1e-9, "4000 → 7200");
        });

        run("聚合:步长对非法输入返回 1", () =>
        {
            TestHelpers.AssertTrue(Math.Abs(TrendAggregator.SnapToStep(0) - 1) < 1e-9, "0 → 1");
            TestHelpers.AssertTrue(Math.Abs(TrendAggregator.SnapToStep(-5) - 1) < 1e-9, "负数 → 1");
            TestHelpers.AssertTrue(Math.Abs(TrendAggregator.SnapToStep(double.NaN) - 1) < 1e-9, "NaN → 1");
        });
    }

    private static void RunMedian(Action<string, Action> run)
    {
        run("聚合:中位间隔 = 10 秒", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 10, 0, 0);
            var raw = Series(t0, 100, 10, i => i);

            var median = TrendAggregator.MedianInterval(raw);

            TestHelpers.AssertTrue(Math.Abs(median - 10) < 0.01, "中位间隔应为 10,实际 " + median);
        });

        run("聚合:少于 2 个点中位间隔为 0", () =>
        {
            TestHelpers.AssertTrue(TrendAggregator.MedianInterval(new List<HistoryRecord>()) == 0, "空列表应为 0");

            var one = new List<HistoryRecord> { Rec(DateTime.Now, 1) };
            TestHelpers.AssertTrue(TrendAggregator.MedianInterval(one) == 0, "单点应为 0");
        });
    }

    private static void RunAutoInterval(Action<string, Action> run)
    {
        run("聚合:自动分辨率不细于原始采样间隔", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 10, 0, 0);
            var to = t0.AddMinutes(10);

            // 原始间隔 10 秒,10 分钟 = 600 秒 / 1500 目标点 = 0.4 秒
            // 但绝不能比 10 秒更细 —— 否则会伪造精度
            var raw = Series(t0, 61, 10, i => i);

            var interval = TrendAggregator.ResolveAutoInterval(t0, to, raw);

            TestHelpers.AssertTrue(interval >= 10,
                "自动间隔不应小于原始 10 秒,实际 " + interval);
        });

        run("聚合:自动分辨率随跨度放大", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 0, 0, 0);

            // 7 天跨度,原始 10 秒间隔
            var to = t0.AddDays(7);
            var raw = Series(t0, 1000, 600, i => i); // 600 秒间隔的稀疏数据

            var interval = TrendAggregator.ResolveAutoInterval(t0, to, raw);

            // 7 天 = 604800 秒 / 1500 = 403 秒 → 规整到 600
            TestHelpers.AssertTrue(interval >= 400 && interval <= 900,
                "7 天跨度应在 400~900 秒之间,实际 " + interval);
        });

        run("聚合:跨度极小时自动分辨率兜底为 1 秒", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 10, 0, 0);
            var raw = new List<HistoryRecord>();

            var interval = TrendAggregator.ResolveAutoInterval(t0, t0, raw);

            TestHelpers.AssertTrue(interval >= 1, "零跨度应兜底,实际 " + interval);
        });

        run("聚合:点数少于目标值时自动模式不聚合(保留原始点)", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 10, 0, 0);
            var to = t0.AddHours(1);

            // 1 小时 / 11 秒 ≈ 327 点,远少于 1500 目标点
            var raw = Series(t0, 327, 11, i => i);

            var buckets = TrendAggregator.Process(
                raw, t0, to, TrendResolution.Auto, TrendAggregate.Average, out var interval);

            TestHelpers.AssertTrue(interval == 0, "点数不足时不应聚合,interval 应为 0,实际 " + interval);
            TestHelpers.AssertEq(327, buckets.Count, "应保留全部 327 个原始点");

            // 原始模式时间戳精确对齐采样时刻,不是桶边界
            TestHelpers.AssertTrue(buckets[1].Time == raw[1].SampleTime, "时间戳应是原始采样时刻");
        });

        run("聚合:点数超过目标值时自动模式才聚合", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 10, 0, 0);
            var to = t0.AddHours(1);

            // 1 小时 / 1 秒 = 3600 点,超过 1500
            var raw = Series(t0, 3600, 1, i => i);

            var buckets = TrendAggregator.Process(
                raw, t0, to, TrendResolution.Auto, TrendAggregate.Average, out var interval);

            TestHelpers.AssertTrue(interval > 0, "点数超限应聚合,实际 interval=" + interval);
            TestHelpers.AssertTrue(buckets.Count < 3600, "聚合后桶数应少于原始点数");
        });
    }

    private static void RunAggregate(Action<string, Action> run)
    {
        var t0 = new DateTime(2026, 11, 4, 10, 0, 0);

        // 10 秒一个桶,每桶 3 个点: 1,2,3 / 4,5,6
        var raw = new List<HistoryRecord>
        {
            Rec(t0, 1), Rec(t0.AddSeconds(2), 2), Rec(t0.AddSeconds(4), 3),
            Rec(t0.AddSeconds(10), 4), Rec(t0.AddSeconds(12), 5), Rec(t0.AddSeconds(14), 6)
        };

        var to = t0.AddSeconds(20);

        run("聚合:平均值", () =>
        {
            var b = TrendAggregator.Aggregate(raw, t0, to, 10, TrendAggregate.Average);

            TestHelpers.AssertEq(3, b[0].Count, "第一桶应含 3 个点(1,2,3)");
            // 桶 0: 1,2,3 → 平均 2
            TestHelpers.AssertTrue(Math.Abs(b[0].Value - 2) < 1e-9, "桶0 平均应为 2,实际 " + b[0].Value);
            // 桶 1: 4,5,6 → 平均 5
            TestHelpers.AssertTrue(Math.Abs(b[1].Value - 5) < 1e-9, "桶1 平均应为 5,实际 " + b[1].Value);
        });

        run("聚合:最小值", () =>
        {
            var b = TrendAggregator.Aggregate(raw, t0, to, 10, TrendAggregate.Minimum);

            TestHelpers.AssertTrue(Math.Abs(b[0].Value - 1) < 1e-9, "桶0 最小应为 1,实际 " + b[0].Value);
        });

        run("聚合:最大值", () =>
        {
            var b = TrendAggregator.Aggregate(raw, t0, to, 10, TrendAggregate.Maximum);

            TestHelpers.AssertTrue(Math.Abs(b[0].Value - 3) < 1e-9, "桶0 最大应为 3,实际 " + b[0].Value);
        });

        run("聚合:首值", () =>
        {
            var b = TrendAggregator.Aggregate(raw, t0, to, 10, TrendAggregate.First);
            TestHelpers.AssertTrue(Math.Abs(b[0].Value - 1) < 1e-9, "桶0 首值应为 1");
        });

        run("聚合:末值", () =>
        {
            var b = TrendAggregator.Aggregate(raw, t0, to, 10, TrendAggregate.Last);
            TestHelpers.AssertTrue(Math.Abs(b[0].Value - 3) < 1e-9, "桶0 末值应为 3");
        });

        run("聚合:求和(累积量必须用这个)", () =>
        {
            var b = TrendAggregator.Aggregate(raw, t0, to, 10, TrendAggregate.Sum);
            TestHelpers.AssertTrue(Math.Abs(b[0].Value - 6) < 1e-9, "桶0 求和应为 1+2+3=6,实际 " + b[0].Value);
        });

        run("聚合:坏质量点被跳过", () =>
        {
            var t = new DateTime(2026, 11, 4, 11, 0, 0);

            var bad = new List<HistoryRecord>
            {
                Rec(t, 1),
                Rec(t.AddSeconds(2), 999, HistoryQuality.Bad),  // 应被跳过
                Rec(t.AddSeconds(4), 3)
            };

            var b = TrendAggregator.Aggregate(bad, t, t.AddSeconds(10), 10, TrendAggregate.Maximum);

            TestHelpers.AssertTrue(Math.Abs(b[0].Max - 3) < 1e-9,
                "坏值 999 不应参与,最大值应为 3,实际 " + b[0].Max);
        });

        run("聚合:NaN 值被跳过", () =>
        {
            var t = new DateTime(2026, 11, 4, 12, 0, 0);

            var withNaN = new List<HistoryRecord>
            {
                Rec(t, 5),
                Rec(t.AddSeconds(2), double.NaN),
                Rec(t.AddSeconds(4), 7)
            };

            var b = TrendAggregator.Aggregate(withNaN, t, t.AddSeconds(10), 10, TrendAggregate.Average);

            TestHelpers.AssertTrue(Math.Abs(b[0].Value - 6) < 1e-9,
                "NaN 应被跳过,平均应为 (5+7)/2=6,实际 " + b[0].Value);
        });
    }

    private static void RunEmptyBucket(Action<string, Action> run)
    {
        run("聚合:空桶写 NaN 而不是 0(绝不能补 0)", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 13, 0, 0);

            // 只有桶 0 和桶 3 有数据,桶 1、2 是空的
            var raw = new List<HistoryRecord>
            {
                Rec(t0, 10),
                Rec(t0.AddSeconds(30), 40)
            };

            var b = TrendAggregator.Aggregate(raw, t0, t0.AddSeconds(50), 10, TrendAggregate.Average);

            TestHelpers.AssertTrue(b[0].Count == 1, "桶0 应有数据");
            TestHelpers.AssertTrue(b[1].IsEmpty, "桶1 应空");
            TestHelpers.AssertTrue(double.IsNaN(b[1].Value), "空桶值必须是 NaN,不能是 0");
            TestHelpers.AssertTrue(b[3].Count == 1, "桶3 应有数据");
        });

        run("聚合:全空区间返回全 NaN 桶", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 14, 0, 0);

            var b = TrendAggregator.Aggregate(
                new List<HistoryRecord>(), t0, t0.AddSeconds(30), 10, TrendAggregate.Average);

            TestHelpers.AssertEq(0, b.Count, "无数据应返回空列表");
        });
    }

    private static void RunEnvelope(Action<string, Action> run)
    {
        run("包络:每桶同时保留最小和最大", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 15, 0, 0);

            // 同一桶内: 5, 200, 8 —— 平均值会被 200 拉高但更常见的是反过来被削平
            var raw = new List<HistoryRecord>
            {
                Rec(t0, 5),
                Rec(t0.AddSeconds(3), 200),
                Rec(t0.AddSeconds(6), 8)
            };

            var b = TrendAggregator.Aggregate(raw, t0, t0.AddSeconds(10), 10, TrendAggregate.Average);

            TestHelpers.AssertTrue(Math.Abs(b[0].Value - 71.0) < 0.1, "平均值应约 71,实际 " + b[0].Value);
            TestHelpers.AssertTrue(Math.Abs(b[0].Min - 5) < 1e-9, "最小应为 5,实际 " + b[0].Min);
            TestHelpers.AssertTrue(Math.Abs(b[0].Max - 200) < 1e-9, "最大应为 200,实际 " + b[0].Max);
        });

        run("包络:最小值聚合时包络仍记录真实最大", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 16, 0, 0);

            var raw = new List<HistoryRecord>
            {
                Rec(t0, 100),
                Rec(t0.AddSeconds(3), 2),
                Rec(t0.AddSeconds(6), 50)
            };

            var b = TrendAggregator.Aggregate(raw, t0, t0.AddSeconds(10), 10, TrendAggregate.Minimum);

            TestHelpers.AssertTrue(Math.Abs(b[0].Value - 2) < 1e-9, "代表值应是最小 2");
            TestHelpers.AssertTrue(Math.Abs(b[0].Max - 100) < 1e-9, "但包络最大仍是 100");
        });
    }

    private static void RunThinRaw(Action<string, Action> run)
    {
        run("原始模式:小数据量不抽样", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 17, 0, 0);
            var raw = Series(t0, 100, 10, i => i);

            var b = TrendAggregator.ThinRaw(raw, t0, t0.AddHours(1));

            TestHelpers.AssertEq(100, b.Count, "100 点应全部保留");
        });

        run("原始模式:min=max=value", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 18, 0, 0);
            var raw = Series(t0, 10, 10, i => i * 1.5);

            var b = TrendAggregator.ThinRaw(raw, t0, t0.AddHours(1));

            foreach (var x in b)
            {
                TestHelpers.AssertTrue(Math.Abs(x.Min - x.Value) < 1e-9, "原始模式包络无意义,min 应等于 value");
                TestHelpers.AssertTrue(Math.Abs(x.Max - x.Value) < 1e-9, "原始模式 max 应等于 value");
            }
        });

        run("原始模式:坏质量点被过滤", () =>
        {
            var t0 = new DateTime(2026, 11, 4, 19, 0, 0);

            var raw = new List<HistoryRecord>
            {
                Rec(t0, 1),
                Rec(t0.AddSeconds(10), 2, HistoryQuality.Bad),
                Rec(t0.AddSeconds(20), 3)
            };

            var b = TrendAggregator.ThinRaw(raw, t0, t0.AddHours(1));

            TestHelpers.AssertEq(2, b.Count, "坏值应被过滤,剩 2 条");
        });
    }
}