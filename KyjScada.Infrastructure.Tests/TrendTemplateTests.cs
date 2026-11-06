using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 分析模板 ini 读写测试。
/// 重点:往返保真、非法输入不抛异常、模板名消毒(防伪造节结构)。
/// </summary>
public static class TrendTemplateTests
{
    public static void RunAll(Action<string, Action> run)
    {
        run("模板:序列化与解析往返保真", () =>
        {
            var src = new List<TrendTemplate>
            {
                new("早班监控", new List<string> { "LQT_Level", "LQT_InPre", "LQT_InTemp" },
                    1, 0, 0, true, false, true),
                new("液位专项", new List<string> { "LQT_Level" },
                    5, 5, 1, false, true, false)
            };

            var text = TrendTemplateStore.Serialize(src);
            var back = TrendTemplateStore.Parse(text);

            TestHelpers.AssertEq(2, back.Count, "应解析出 2 个模板");

            TestHelpers.AssertStr("早班监控", back[0].Name, "名称");
            TestHelpers.AssertEq(3, back[0].Variables.Count, "变量数");
            TestHelpers.AssertStr("LQT_Level", back[0].Variables[0], "首变量");
            TestHelpers.AssertEq(1, back[0].RangeIndex, "Range");
            TestHelpers.AssertEq(0, back[0].ResolutionIndex, "Resolution");
            TestHelpers.AssertEq(0, back[0].AggregateIndex, "Aggregate");
            TestHelpers.AssertTrue(back[0].ShowEnvelope, "Envelope");
            TestHelpers.AssertTrue(!back[0].ShowLimits, "Limits");
            TestHelpers.AssertTrue(back[0].ShowEvents, "Events");

            TestHelpers.AssertStr("液位专项", back[1].Name, "第二个模板名");
            TestHelpers.AssertEq(5, back[1].RangeIndex, "第二个 Range");
            TestHelpers.AssertEq(5, back[1].ResolutionIndex, "第二个 Resolution");
            TestHelpers.AssertTrue(!back[1].ShowEnvelope, "第二个 Envelope 应为 false");
            TestHelpers.AssertTrue(back[1].ShowLimits, "第二个 Limits 应为 true");
        });

        run("模板:空文本返回空列表", () =>
        {
            TestHelpers.AssertEq(0, TrendTemplateStore.Parse(null).Count, "null 应为空");
            TestHelpers.AssertEq(0, TrendTemplateStore.Parse("").Count, "空串应为空");
            TestHelpers.AssertEq(0, TrendTemplateStore.Parse("   \n\n  ").Count, "空白应为空");
        });

        run("模板:缺字段取默认值", () =>
        {
            var text = "[只写了变量]\nVariables=A;B\n";
            var list = TrendTemplateStore.Parse(text);

            TestHelpers.AssertEq(1, list.Count, "应解析出 1 个");
            TestHelpers.AssertEq(2, list[0].Variables.Count, "变量数");
            TestHelpers.AssertEq(1, list[0].RangeIndex, "Range 默认 1");
            TestHelpers.AssertEq(0, list[0].ResolutionIndex, "Resolution 默认 0");
            TestHelpers.AssertTrue(list[0].ShowEnvelope, "Envelope 默认 true");
            TestHelpers.AssertTrue(!list[0].ShowLimits, "Limits 默认 false");
        });

        run("模板:非法数字取默认值而不是抛异常", () =>
        {
            var text = "[X]\nVariables=A\nRange=abc\nResolution=-999\nAggregate=xyz\n";
            var list = TrendTemplateStore.Parse(text);

            TestHelpers.AssertEq(1, list.Count, "应解析出 1 个");
            TestHelpers.AssertEq(1, list[0].RangeIndex, "非法 Range 应回落默认 1");
            TestHelpers.AssertEq(-999, list[0].ResolutionIndex, "整数能解析就保留(上层做边界检查)");
        });

        run("模板:注释行被忽略", () =>
        {
            var text = "; 这是注释\n# 这也是\n[A]\n; 中间注释\nVariables=A\n";
            var list = TrendTemplateStore.Parse(text);

            TestHelpers.AssertEq(1, list.Count, "注释不影响解析");
            TestHelpers.AssertEq(1, list[0].Variables.Count, "变量数");
        });

        run("模板:变量分隔符支持分号和逗号", () =>
        {
            var text = "[X]\nVariables=A,B;C,D\n";
            var list = TrendTemplateStore.Parse(text);

            TestHelpers.AssertEq(4, list[0].Variables.Count, "应解析出 4 个变量");
        });

        run("模板:名称消毒防伪造节结构", () =>
        {
            TestHelpers.AssertStr("(坏)名字", TrendTemplateStore.SanitizeName("[坏]名字"), "方括号应替换");
            TestHelpers.AssertStr("未命名", TrendTemplateStore.SanitizeName(""), "空名");
            TestHelpers.AssertStr("未命名", TrendTemplateStore.SanitizeName("   "), "空白名");

            // 换行会让一个模板变成两个节,必须清掉
            var sanitized = TrendTemplateStore.SanitizeName("A\n[B]");
            TestHelpers.AssertTrue(sanitized.IndexOf('\n') < 0, "不应含换行");
        });

        run("模板:超长名称被截断", () =>
        {
            var longName = new string ('x', 100);
            var s = TrendTemplateStore.SanitizeName(longName);

            TestHelpers.AssertTrue(s.Length <= 40, "应截断到 40 字符以内,实际 " + s.Length);
        });

        run("模板:名称含方括号时序列化后仍可解析回原名", () =>
        {
            var src = new List<TrendTemplate>
            {
                new("[特殊]模板", new List<string> { "A" }, 1, 0, 0, true, false, true)
            };

            var text = TrendTemplateStore.Serialize(src);
            var back = TrendTemplateStore.Parse(text);

            TestHelpers.AssertEq(1, back.Count, "应解析出 1 个");
            TestHelpers.AssertStr("(特殊)模板", back[0].Name, "名称应被消毒为圆括号");
        });
    }
}