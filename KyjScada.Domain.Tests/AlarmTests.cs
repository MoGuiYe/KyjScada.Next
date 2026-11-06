using KyjScada.Contracts;
using KyjScada.Domain.Alarm;

namespace KyjScada.Domain.Tests;

public static class AlarmTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 8, 0, 0);

    public static void RunAll(TestRunner t)
    {
        t.Run("高报:超过阈值才触发", () =>
        {
            var m = new AlarmStateMachine(AlarmDirection.High, 54.0);

            Assert.True(m.Update(50.0, T0) is null, "50 < 54 不应触发");
            Assert.True(m.Update(54.0, T0) is null, "等于阈值不应触发(必须严格超过)");

            var tr = m.Update(55.0, T0);
            Assert.True(tr is not null, "55 > 54 应触发");
            Assert.Equal(AlarmLevel.Active, tr!.To, "应进入 Active");
            Assert.Equal(AlarmLevel.Normal, tr.From, "应从 Normal 来");
            Assert.Near(55.0, tr.Value, "转移值");
        });

        t.Run("高报:无死区时降到阈值以下就消除", () =>
        {
            var m = new AlarmStateMachine(AlarmDirection.High, 54.0, 0d);

            m.Update(55.0, T0);
            var tr = m.Update(53.9, T0);

            Assert.True(tr is not null, "53.9 < 54 应消除");
            Assert.Equal(AlarmLevel.Normal, tr!.To, "应回到 Normal");
        });

        t.Run("高报:死区抑制抖动(核心场景)", () =>
        {
            // 实测老系统液位在 54 附近反复穿越,触发 181 次、消除 383 次
            var m = new AlarmStateMachine(AlarmDirection.High, 54.0, 2.0);

            // 先触发
            Assert.True(m.Update(55.0, T0) is not null, "55 应触发");

            // 在死区内反复抖动,都不应产生转移
            Assert.True(m.Update(53.5, T0) is null, "53.5 在死区 [52,54] 内,不应消除");
            Assert.True(m.Update(54.5, T0) is null, "54.5 在死区内,不应重新触发");
            Assert.True(m.Update(52.5, T0) is null, "52.5 在死区内,不应消除");
            Assert.True(m.Update(53.9, T0) is null, "53.9 在死区内,不应消除");

            // 真正降到死区外才消除
            var tr = m.Update(51.0, T0);
            Assert.True(tr is not null, "51 < 52 应消除");
            Assert.Equal(AlarmLevel.Normal, tr!.To, "应回到 Normal");
        });

        t.Run("高报:死区边界值", () =>
        {
            var m = new AlarmStateMachine(AlarmDirection.High, 54.0, 2.0);
            m.Update(55.0, T0);

            // 消除线是 52,严格小于才消除
            Assert.True(m.Update(52.0, T0) is null, "等于消除线 52 应保持报警(必须严格小于)");

            var tr = m.Update(51.99, T0);
            Assert.True(tr is not null, "51.99 < 52 应消除");
        });

        t.Run("低报:低于阈值才触发", () =>
        {
            var m = new AlarmStateMachine(AlarmDirection.Low, 5.0);

            Assert.True(m.Update(6.0, T0) is null, "6 > 5 不应触发");
            Assert.True(m.Update(5.0, T0) is null, "等于阈值不应触发");

            var tr = m.Update(4.0, T0);
            Assert.True(tr is not null, "4 < 5 应触发");
            Assert.Equal(AlarmLevel.Active, tr!.To, "应进入 Active");
        });

        t.Run("低报:死区抑制抖动", () =>
        {
            var m = new AlarmStateMachine(AlarmDirection.Low, 5.0, 1.0);

            m.Update(4.0, T0);

            // 死区 [5,6],升到 6 以下都不消除
            Assert.True(m.Update(5.5, T0) is null, "5.5 在死区内,不应消除");
            Assert.True(m.Update(4.5, T0) is null, "4.5 仍在报警,不应消除");

            var tr = m.Update(6.5, T0);
            Assert.True(tr is not null, "6.5 > 6 应消除");
            Assert.Equal(AlarmLevel.Normal, tr!.To, "应回到 Normal");
        });

        t.Run("无效值不改变状态", () =>
        {
            var m = new AlarmStateMachine(AlarmDirection.High, 54.0, 2.0);

            m.Update(55.0, T0);

            Assert.True(m.Update(null, T0) is null, "null 不应产生转移");
            Assert.Equal(AlarmLevel.Active, m.Level, "状态应保持 Active");

            Assert.True(m.Update(double.NaN, T0) is null, "NaN 不应产生转移");
            Assert.Equal(AlarmLevel.Active, m.Level, "状态应保持 Active");
        });

        t.Run("无转移时不产生事件", () =>
        {
            var m = new AlarmStateMachine(AlarmDirection.High, 54.0);

            Assert.True(m.Update(50.0, T0) is null, "正常值不产生事件");
            Assert.True(m.Update(51.0, T0) is null, "仍正常,不产生事件");
            Assert.Equal(AlarmLevel.Normal, m.Level, "应保持 Normal");
        });

        t.Run("手动复位", () =>
        {
            var m = new AlarmStateMachine(AlarmDirection.High, 54.0);

            m.Update(55.0, T0);
            Assert.Equal(AlarmLevel.Active, m.Level, "应先触发");

            var tr = m.Reset(T0);
            Assert.True(tr is not null, "复位应产生事件");
            Assert.Equal(AlarmLevel.Normal, m.Level, "复位后应 Normal");

            Assert.True(m.Reset(T0) is null, "已 Normal 时复位无事件");
        });

        t.Run("负死区被拒绝", () =>
        {
            Assert.Throws(() => new AlarmStateMachine(AlarmDirection.High, 54.0, -1.0), "负死区");
        });

        t.Run("抖动抑制量化:100 次穿越只产生 1 次触发", () =>
        {
            // 模拟液位在 54 附近抖动:52.5 ~ 54.8 反复
            var m = new AlarmStateMachine(AlarmDirection.High, 54.0, 2.0);

            var events = 0;
            var values = new[] { 54.8, 53.5, 54.8, 53.5, 54.8, 53.5, 52.9, 54.8, 53.1 };

            for (int i = 0; i < 100; i++)
            {
                var v = values[i % values.Length];
                if (m.Update(v, T0) is not null)
                {
                    events++;
                }
            }

            // 第一次 54.8 触发 1 次,之后全在死区内不转移
            Assert.Equal(1, events, "100 次抖动应只产生 1 次事件");
        });

        t.Run("无死区对照:同样抖动产生大量事件", () =>
        {
            var m = new AlarmStateMachine(AlarmDirection.High, 54.0, 0d);

            var events = 0;
            var values = new[] { 54.8, 53.5, 54.8, 53.5 };

            for (int i = 0; i < 100; i++)
            {
                var v = values[i % values.Length];
                if (m.Update(v, T0) is not null)
                {
                    events++;
                }
            }

            Assert.True(events >= 99, "无死区时应几乎每次都产生事件,实际 " + events);
        });
    }
}
