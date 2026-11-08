using System.Text;

namespace KyjScada.Runtime;

/// <summary>
/// 分析模板:一组变量 + 显示参数。目的是让操作员不用每天重复勾同样的十几个位号。
/// </summary>
public sealed record TrendTemplate(
    string Name,
    List<string> Variables,
    int RangeIndex,
    int ResolutionIndex,
    int AggregateIndex,
    bool ShowEnvelope,
    bool ShowLimits,
    bool ShowEvents);

/// <summary>
/// 模板的 ini 文本读写。放 Runtime 便于单测,不掺文件 IO。
///
/// 格式(节名即模板名):
///   [早班监控]
///   Variables=LQT_Level;LQT_InPre;LQT_InTemp
///   Range=1
///   Resolution=0
///   Aggregate=0
///   Envelope=1
///   Limits=0
///   Events=1
/// </summary>
public static class TrendTemplateStore
{
    /// <summary>解析 ini 文本。非法节/缺字段一律跳过或取默认值,不抛异常。</summary>
    public static List<TrendTemplate> Parse(string? text)
    {
        var list = new List<TrendTemplate>();

        if (string.IsNullOrWhiteSpace(text)) return list;

        string? name = null;
        var vars = new List<string>();
        var range = 1;
        var res = 0;
        var agg = 0;
        var envelope = true;
        var limits = false;
        var events = true;

        void Flush()
        {
            if (name is null) return;

            list.Add(new TrendTemplate(name, vars, range, res, agg, envelope, limits, events));

            name = null;
            vars = new List<string>();
            range = 1;
            res = 0;
            agg = 0;
            envelope = true;
            limits = false;
            events = true;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0) continue;
            if (line.StartsWith(';') || line.StartsWith('#')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                name = line.Substring(1, line.Length - 2).Trim();
                continue;
            }

            if (name is null) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line.Substring(0, eq).Trim();
            var val = line.Substring(eq + 1).Trim();

            switch (key.ToLowerInvariant())
            {
                case "variables":
                    vars = SplitVariables(val);
                    break;

                case "range": range = ToInt(val, 1); break;
                case "resolution": res = ToInt(val, 0); break;
                case "aggregate": agg = ToInt(val, 0); break;
                case "envelope": envelope = ToBool(val, true); break;
                case "limits": limits = ToBool(val, false); break;
                case "events": events = ToBool(val, true); break;
            }
        }

        Flush();

        return list;
    }

    /// <summary>序列化成 ini 文本。模板名里的方括号会被替换,避免破坏节结构。</summary>
    public static string Serialize(IEnumerable<TrendTemplate> templates)
    {
        var sb = new StringBuilder();

        foreach (var t in templates)
        {
            var safeName = SanitizeName(t.Name);

            sb.Append('[').Append(safeName).Append(']').Append('\n');
            // 变量名要按需加引号:名字里带分号/逗号时原来会被原样拼进去,
            // 读回来就被拆成两个不存在的变量;带换行还能伪造出 `Envelope=0`
            // 甚至 `[另一个模板]` 这样的新节 —— 模板文件被打开一次就变形。
            var names = t.Variables
                .Select(QuoteVar)
                .Where(v => v.Length > 0);

            sb.Append("Variables=").Append(string.Join(';', names)).Append('\n');
            sb.Append("Range=").Append(t.RangeIndex).Append('\n');
            sb.Append("Resolution=").Append(t.ResolutionIndex).Append('\n');
            sb.Append("Aggregate=").Append(t.AggregateIndex).Append('\n');
            sb.Append("Envelope=").Append(t.ShowEnvelope ? 1 : 0).Append('\n');
            sb.Append("Limits=").Append(t.ShowLimits ? 1 : 0).Append('\n');
            sb.Append("Events=").Append(t.ShowEvents ? 1 : 0).Append('\n');
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// 变量名按需加双引号(内含分隔符时),内部引号双写。
    /// 换行没有可表示的形式,统一折成空格 —— 宁可名字对不上被跳过,
    /// 也不能让它拼出额外的键行/节行。
    /// </summary>
    public static string QuoteVar(string? varName)
    {
        var s = (varName ?? "").Replace('\r', ' ').Replace('\n', ' ');

        if (s.Length == 0) return "";

        var needQuote = s.IndexOfAny(new[] { ';', ',', '"' }) >= 0 || s != s.Trim();

        if (!needQuote) return s;

        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// 按 ';'/',' 切分变量列表,但**双引号内的分隔符不算**;引号内 "" 表示一个引号。
    /// 老文件里没有引号,行为与原来的 Split 完全一致。
    /// </summary>
    private static List<string> SplitVariables(string val)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        void AddToken()
        {
            var token = sb.ToString().Trim();
            sb.Clear();

            if (token.Length > 0) result.Add(token);
        }

        for (var i = 0; i < val.Length; i++)
        {
            var c = val[i];

            if (inQuotes)
            {
                if (c != '"')
                {
                    sb.Append(c);
                    continue;
                }

                if (i + 1 < val.Length && val[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                    continue;
                }

                inQuotes = false;
                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
                continue;
            }

            if (c == ';' || c == ',')
            {
                AddToken();
                continue;
            }

            sb.Append(c);
        }

        AddToken();

        return result;
    }

    /// <summary>模板名消毒:去掉换行和方括号,防止伪造节结构。</summary>
    public static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "未命名";

        var s = name.Replace('\n', ' ').Replace('\r', ' ').Replace('[', '(').Replace(']', ')');

        s = s.Trim();

        return s.Length == 0 ? "未命名" : (s.Length > 40 ? s.Substring(0, 40) : s);
    }

    private static int ToInt(string s, int fallback)
    {
        return int.TryParse(s, out var v) ? v : fallback;
    }

    private static bool ToBool(string s, bool fallback)
    {
        if (bool.TryParse(s, out var b)) return b;

        if (s == "1") return true;
        if (s == "0") return false;

        return fallback;
    }
}