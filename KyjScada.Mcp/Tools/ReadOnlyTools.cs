using System.ComponentModel;
using System.Globalization;
using KyjScada.Mcp.Data;
using ModelContextProtocol.Server;

namespace KyjScada.Mcp.Tools;

/// <summary>
/// 第一阶段 MCP 工具:全部只读,零风险。
///
/// 设计铁律:
///  1. 窄工具、带语义,不做万能 SQL 口
///  2. NULL 必须显式返回 null,绝不默认 0(LQB1_Start 实测 96% 为 NULL)
///  3. 必须返回时间戳 + 陈旧分级,避免 AI 把归档值当实时值
///  4. 工具名由 C# 方法名自动转 snake_case(ListVariables -> list_variables)
/// </summary>
[McpServerToolType]
public sealed class ReadOnlyTools
{
    private readonly ConfigReader _config;
    private readonly DbReader _db;

    public ReadOnlyTools(ConfigReader config, DbReader db)
    {
        _config = config;
        _db = db;
    }

    /// <summary>
    /// 组态读不出来时回给调用方的文本:必须带上 <see cref="ConfigReader.LastError"/>。
    /// 只回"无法解析组态文件"时,AI 会当成"这台机器没有组态"转头去查别的东西;
    /// 真实原因(文件被占用/JSON 写了一半/空数组)恰恰是排查的唯一线索。
    /// </summary>
    private string ConfigLoadFailure(string fileName)
    {
        var why = _config.LastError;

        return string.IsNullOrWhiteSpace (why)
            ? $"无法解析组态文件: {fileName}"
            : $"无法解析组态文件: {fileName};原因: {why}";
    }

    [McpServerTool, Description("列出监控变量元数据(名称/类型/中文描述/单位/是否归档/报警阈值)。不传 keyword 返回全部。")]
    public string ListVariables(
        [Description("按变量名或中文描述过滤,不区分大小写;留空返回全部")] string? keyword = null)
    {
        // 新库:变量字典在 VarDict,直接查库(名称/设备/单位),不依赖组态 JSON。
        if (_db.IsNewSchema)
        {
            var dict = _db.GetVarDict();
            var names = dict.Keys
                .Where(n => keyword == null ||
                            n.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var nb = new System.Text.StringBuilder();
            nb.AppendLine($"数据模式: 新长表(History/VarDict)。变量总数: {dict.Count},匹配: {names.Count}");
            nb.AppendLine();
            nb.AppendLine("| 变量名 | 设备 | 单位 |");
            nb.AppendLine("|---|---|---|");
            foreach (var n in names.Take(200))
            {
                var (dev, unit) = dict[n];
                nb.AppendLine($"| {n} | {dev ?? "-"} | {unit ?? "-"} |");
            }
            if (names.Count > 200)
                nb.AppendLine($"\n(仅显示前 200 条,共 {names.Count} 条)");
            return nb.ToString();
        }

        var files = _config.ListConfigFiles();
        if (files.Count == 0)
            return "未找到组态文件。settings 目录下没有 *.json。";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"组态文件: {string.Join(", ", files)}");

        // 优先选与实际数据库匹配的文件:现场 config.ini 实测协议=ModbusTCP,
        // 且 ActualData 的 37 个数据列与 TCP 组态最吻合。默认按 ModbusTCP 优先。
        var pick = files.FirstOrDefault(f => f.Contains("TCP", StringComparison.OrdinalIgnoreCase) && !f.Contains("Over", StringComparison.OrdinalIgnoreCase))
                   ?? files[0];

        var project = _config.Load(pick);
        if (project == null)
            return ConfigLoadFailure(pick);

        var all = _config.EnumerateVariables(project).ToList();
        var filtered = keyword == null
            ? all
            : all.Where(v =>
                (v.VarName != null && v.VarName.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                (v.Comments != null && v.Comments.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
              .ToList();

        sb.AppendLine($"项目: {project.ProjectName}");
        sb.AppendLine($"变量总数: {all.Count},匹配: {filtered.Count}");
        sb.AppendLine();
        sb.AppendLine("| 变量名 | 类型 | 中文描述 | 单位 | 归档 | 高报 | 低报 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");

        foreach (var v in filtered.Take(200))
        {
            var hi = v.HighAlarmEnable ? v.HighAlarmValue.ToString("0.##") : "-";
            var lo = v.LowAlarmEnable ? v.LowAlarmValue.ToString("0.##") : "-";
            sb.AppendLine($"| {v.VarName} | {v.VarType} | {v.Comments} | {v.Unit ?? "-"} | {(v.ArchiveEnable ? "是" : "否")} | {hi} | {lo} |");
        }

        if (filtered.Count > 200)
            sb.AppendLine($"\n(仅显示前 200 条,共 {filtered.Count} 条)");

        return sb.ToString();
    }

    [McpServerTool, Description("读取单个变量的最新值。返回原始值、时间戳与数据新鲜度。注意:这是归档快照,非内存实时值。")]
    public string ReadVariable(
        [Description("变量名,例如 LQT_Level、KYJ1_OutTemp")] string name)
    {
        var latest = _db.ReadLatestRow();
        if (latest == null)
            return _db.IsNewSchema
                ? "历史库为空,还没有任何归档数据。"
                : "ActualData 表为空,无任何归档数据。";

        var (insertTime, values, times) = latest.Value;

        string? raw = null;
        foreach (var kv in values)
        {
            if (kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                raw = kv.Value;
                break;
            }
        }

        if (raw == null && !values.Keys.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            var avail = string.Join(", ", values.Keys.Take(20));
            return _db.IsNewSchema
                ? $"变量 {name} 没有归档记录。历史库只存勾选了「归档」的变量(当前 {values.Count} 个),组态里有但没归档的点读不到。已有归档变量(前20): {avail}"
                : $"变量 {name} 不存在于 ActualData 表。可用列(前20): {avail}";
        }

        // 用这个变量自己的归档时间判新鲜度:全库最新时刻代表不了它
        times.TryGetValue(name, out var varTime);
        var stamp = varTime ?? insertTime;

        var freshness = DescribeFreshness(stamp);
        var shown = raw == null ? "NULL(该时刻无记录)" : raw;

        var note = varTime != null && insertTime != null && !string.Equals(varTime, insertTime, StringComparison.Ordinal)
            ? "说明: 该变量的最后归档时间早于全库最新时刻,新鲜度按它自己的时间戳算。\n"
            : "";

        return
            $"变量: {name}\n" +
            $"值: {shown}\n" +
            $"时间戳: {stamp}\n" +
            $"新鲜度: {freshness}\n" +
            note +
            $"说明: 这是归档快照值,不是内存实时值。若新鲜度非 fresh,请勿当作当前状态。";
    }

    [McpServerTool, Description("批量读取多个变量的最新值。")]
    public string ReadVariables(
        [Description("变量名列表,逗号分隔,例如 LQT_Level,KYJ1_OutTemp")] string names)
    {
        var latest = _db.ReadLatestRow();
        if (latest == null)
            return _db.IsNewSchema ? "历史库为空。" : "ActualData 表为空。";

        var (insertTime, values, times) = latest.Value;
        var wanted = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"全库最新时间戳: {insertTime}  新鲜度: {DescribeFreshness(insertTime)}");
        sb.AppendLine("(每个变量按自己的归档时间判新鲜度:同一库里各点的最后归档时刻可能差很远)");
        sb.AppendLine();
        sb.AppendLine("| 变量 | 值 | 时间戳 | 新鲜度 |");
        sb.AppendLine("|---|---|---|---|");

        foreach (var w in wanted)
        {
            string? raw = null;
            var exists = false;

            foreach (var kv in values)
            {
                if (kv.Key.Equals(w, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    raw = kv.Value;
                    break;
                }
            }

            times.TryGetValue(w, out var wTime);
            var shown = !exists ? "(列不存在)" : (raw == null ? "NULL" : raw);
            sb.AppendLine($"| {w} | {shown} | {wTime ?? "(无)"} | {DescribeFreshness(wTime)} |");
        }

        return sb.ToString();
    }

    [McpServerTool, Description("查询历史时序数据。返回指定变量在时间范围内的采样点。")]
    public string QueryHistory(
        [Description("变量名,逗号分隔")] string names,
        [Description("开始时间,格式 yyyy-MM-dd HH:mm:ss")] string start,
        [Description("结束时间,格式 yyyy-MM-dd HH:mm:ss")] string end,
        [Description("最大返回点数,默认 2000")] int limit = 2000)
    {
        DateTime s, e;
        try
        {
            s = DateTime.ParseExact(start, DbReader.TimeFormat, CultureInfo.InvariantCulture);
            e = DateTime.ParseExact(end, DbReader.TimeFormat, CultureInfo.InvariantCulture);
        }
        catch
        {
            return $"时间格式错误。必须为 {DbReader.TimeFormat}";
        }

        if (s > e) return "开始时间晚于结束时间。";

        var cols = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rows = _db.QueryHistory(cols, s, e, limit);

        if (rows.Count == 0)
            return $"在 {start} ~ {end} 范围内,变量 [{names}] 无数据。";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"范围: {start} ~ {end}");
        sb.AppendLine($"返回点数: {rows.Count}(上限 {limit})");
        sb.AppendLine();
        sb.AppendLine($"| 时间 | {string.Join(" | ", cols)} |");
        sb.AppendLine($"|---|{string.Join("|", cols.Select(_ => "---"))}|");

        // 采样显示:超过 60 点则等间隔抽样,避免刷屏
        var step = rows.Count > 60 ? rows.Count / 60 : 1;
        for (int i = 0; i < rows.Count; i += step)
        {
            var r = rows[i];
            var cells = cols.Select(c => r.Values.TryGetValue(c, out var v) ? (v ?? "NULL") : "-");
            sb.AppendLine($"| {r.InsertTime} | {string.Join(" | ", cells)} |");
        }

        if (step > 1) sb.AppendLine($"\n(原始 {rows.Count} 点,已等间隔抽样显示)");

        return sb.ToString();
    }

    [McpServerTool, Description("统计变量在时间范围内的行数/最小值/最大值/平均值。自动过滤脏行(如 '--')。")]
    public string QueryStat(
        [Description("变量名")] string name,
        [Description("开始时间,格式 yyyy-MM-dd HH:mm:ss")] string start,
        [Description("结束时间,格式 yyyy-MM-dd HH:mm:ss")] string end)
    {
        DateTime s, e;
        try
        {
            s = DateTime.ParseExact(start, DbReader.TimeFormat, CultureInfo.InvariantCulture);
            e = DateTime.ParseExact(end, DbReader.TimeFormat, CultureInfo.InvariantCulture);
        }
        catch
        {
            return $"时间格式错误。必须为 {DbReader.TimeFormat}";
        }

        var stat = _db.QueryStat(name, s, e);
        if (stat == null)
            return _db.IsNewSchema
                ? $"变量 {name} 不在 VarDict 中。可用 list_variables 查看全部变量。"
                : $"变量 {name} 不是 ActualData 的数据列。";

        if (stat.Count == 0) return $"在 {start} ~ {end} 范围内,变量 {name} 无有效数值。";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"变量: {name}");
        sb.AppendLine($"范围: {start} ~ {end}");
        sb.AppendLine($"有效行数: {stat.Count}");
        sb.AppendLine($"最小值: {stat.Min?.ToString("0.###") ?? "-"}");
        sb.AppendLine($"最大值: {stat.Max?.ToString("0.###") ?? "-"}");
        sb.AppendLine($"平均值: {stat.Avg?.ToString("0.###") ?? "-"}");

        var warn = CheckAnomaly(name, stat);
        if (warn != null)
        {
            sb.AppendLine();
            sb.AppendLine("警告(数据质量): " + warn);
        }

        return sb.ToString();
    }

    private string? CheckAnomaly(string name, StatResult stat)
    {
        var files = _config.ListConfigFiles();
        if (files.Count == 0) return null;

        var pick = files.FirstOrDefault(f => f.Contains("TCP", StringComparison.OrdinalIgnoreCase) && !f.Contains("Over", StringComparison.OrdinalIgnoreCase))
                   ?? files[0];

        var project = _config.Load(pick);
        if (project == null) return null;

        var v = _config.EnumerateVariables(project)
            .FirstOrDefault(x => x.VarName != null && x.VarName.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (v == null || !v.HighAlarmEnable) return null;
        if (stat.Max == null) return null;

        if (v.HighAlarmValue > 0 && stat.Max > v.HighAlarmValue * 10)
        {
            return
                $"最大值 {stat.Max:0.###} 远超组态上限 {v.HighAlarmValue:0.###}(超过 10 倍)," +
                $"库中很可能存在采集异常或错位读数。请勿直接用平均值或最大值做判断," +
                $"建议先用 query_history 抽样确认正常区间。";
        }

        return null;
    }

    [McpServerTool, Description("查询报警与操作日志。LogType 可为 系统日志/操作日志/系统报警,留空查全部。")]
    public string QueryAlarms(
        [Description("开始时间,格式 yyyy-MM-dd HH:mm:ss;留空=最近 24 小时")] string? start = null,
        [Description("结束时间,格式 yyyy-MM-dd HH:mm:ss;留空=现在")] string? end = null,
        [Description("日志类型,可选:系统日志/操作日志/系统报警")] string? logType = null,
        [Description("最大返回条数,默认 100")] int limit = 100)
    {
        if (limit <= 0) limit = 100;

        // 上限必须钳住:limit 直通 SQL,LIMIT int.MaxValue 会把整张日志读进内存,
        // 而 MCP 客户端传大数是很常见的(它们只按 schema 填参,不看语义)。
        if (limit > 500) limit = 500;

        // 不传时间就查最近 24 小时:调用方(尤其是自动化客户端)经常只给工具名,不给参数,
        // 以前会直接抛 ArgumentNullException,协议层只回一句 "An error occurred",看不出问题。
        //
        // 注意"最近 24 小时"不能写成 Date.AddDays(-1) ~ Date.AddDays(1):
        // 那是昨天 0 点到明天 23:59,跨度 48 小时且把未来时间也算进去,
        // 与工具说明里的"最近 24 小时"不符,报警条数还会被无端放大一倍。
        var now = DateTime.Now;

        DateTime s, e;
        try
        {
            s = string.IsNullOrWhiteSpace(start)
                ? now.AddHours(-24)
                : DateTime.ParseExact(start, DbReader.TimeFormat, CultureInfo.InvariantCulture);
            e = string.IsNullOrWhiteSpace(end)
                ? now
                : DateTime.ParseExact(end, DbReader.TimeFormat, CultureInfo.InvariantCulture);
        }
        catch
        {
            return $"时间格式错误。必须为 {DbReader.TimeFormat}";
        }

        if (e < s) return "时间范围错误:结束时间早于开始时间。";

        List<LogRow> logs;
        try
        {
            logs = _db.QueryLogs(s, e, logType, limit);
        }
        catch (Exception ex)
        {
            // 采集正在高频写库时,读连接可能被锁:给出可读原因,而不是让协议层报未知错误
            return $"查询失败: {ex.Message}";
        }

        var sText = s.ToString(DbReader.TimeFormat, CultureInfo.InvariantCulture);
        var eText = e.ToString(DbReader.TimeFormat, CultureInfo.InvariantCulture);

        if (logs.Count == 0)
            return $"在 {sText} ~ {eText} 范围内无日志记录。";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"返回 {logs.Count} 条");
        sb.AppendLine();
        sb.AppendLine("| 时间 | 类型 | 操作员 | 内容 | 变量 | 报警值 |");
        sb.AppendLine("|---|---|---|---|---|---|");

        foreach (var l in logs)
        {
            sb.AppendLine($"| {l.InsertTime} | {l.LogType} | {l.Operator} | {l.Note} | {l.VarName ?? "-"} | {l.AlarmValue ?? "-"} |");
        }

        return sb.ToString();
    }

    [McpServerTool, Description("获取设备与数据库概况:组态设备清单、变量统计、数据时间跨度、数据列清单。")]
    public string GetDeviceSummary()
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# 组态概况");
        var files = _config.ListConfigFiles();
        sb.AppendLine($"组态文件: {string.Join(", ", files)}");

        if (files.Count > 0)
        {
            var pick = files.FirstOrDefault(f => f.Contains("TCP", StringComparison.OrdinalIgnoreCase) && !f.Contains("Over", StringComparison.OrdinalIgnoreCase))
                       ?? files[0];

            var project = _config.Load(pick);
            if (project != null)
            {
                sb.AppendLine($"使用组态: {pick}");
                var vars = _config.EnumerateVariables(project).ToList();
                var archived = vars.Count(v => v.ArchiveEnable);

                sb.AppendLine($"项目名: {project.ProjectName}");
                sb.AppendLine($"TCP 设备数: {project.ModbusTCPList.Count}");
                sb.AppendLine($"变量总数: {vars.Count}(其中归档 {archived})");

                foreach (var dev in project.ModbusTCPList)
                {
                    sb.AppendLine($"  设备: {dev.DeviceName}  {dev.IpAddress}:{dev.Port}  激活={dev.IsActive}");
                    foreach (var g in dev.GroupList)
                    {
                        sb.AppendLine($"    组: {g.GroupName}  起始={g.Start}  长度={g.Length}  变量={g.VarList.Count}");
                    }
                }
            }
            else
            {
                // 读不出来要说清是"文件坏了"还是"文件没有":只说"没组态",
                // AI 会当成"这台机器没配置",然后去查不存在的数据。
                sb.AppendLine(ConfigLoadFailure(pick));
            }
        }

        sb.AppendLine();
        sb.AppendLine("# 数据库概况");

        var summary = _db.GetSummary();
        sb.AppendLine($"模式: {summary.Schema}");
        sb.AppendLine($"历史行数: {summary.ActualDataRows}");
        sb.AppendLine($"时间跨度: {summary.MinTime} ~ {summary.MaxTime}");
        sb.AppendLine($"报警/日志行数: {summary.SysLogRows}");
        sb.AppendLine($"数据列数: {summary.DataColumns.Count}");

        if (_db.IsNewSchema)
        {
            // 新库:从 VarDict 展示变量-设备归属,不依赖组态 JSON
            var dict = _db.GetVarDict();
            var byDev = dict.Values.GroupBy(v => v.Device ?? "(未归档设备)").OrderBy(g => g.Key);
            foreach (var g in byDev)
                sb.AppendLine($"  设备 {g.Key}: {g.Count()} 变量");
        }
        else
        {
            sb.AppendLine($"数据列: {string.Join(", ", summary.DataColumns)}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 陈旧分级。方案缺实时值时的诚实补偿:
    /// 宁可告诉 AI「数据是 3 分钟前的」,也不能让 AI 以为是当前值。
    /// </summary>
    private static string DescribeFreshness(string? insertTime)
    {
        if (insertTime == null) return "unknown(无时间戳)";

        DateTime t;
        try
        {
            t = DateTime.ParseExact(insertTime, DbReader.TimeFormat, CultureInfo.InvariantCulture);
        }
        catch
        {
            return $"unknown(时间戳无法解析: {insertTime})";
        }

        var age = DateTime.Now - t;

        // 时间戳在未来:可能是设备/系统时钟错乱,也可能是有人构造的数据。
        // 原来拿负的 age 去比 <10,会直接报成 "fresh(约 0 秒前)" —— 最坏的一种误判。
        if (age.TotalSeconds < -5)
            return $"unknown(时间戳在未来 {(-age).TotalMinutes:0} 分钟,请检查系统/设备时钟)";

        // 轻微超前(几秒)是时钟抖动,当刚采到处理
        if (age.TotalSeconds < 0) age = TimeSpan.Zero;

        if (age.TotalSeconds < 10) return $"fresh(约 {age.TotalSeconds:0} 秒前)";
        if (age.TotalSeconds < 60) return $"stale(约 {age.TotalSeconds:0} 秒前,可能采集中断)";
        if (age.TotalMinutes < 60) return $"offline(约 {age.TotalMinutes:0} 分钟前,数据已停)";
        if (age.TotalHours < 24) return $"offline(约 {age.TotalHours:0} 小时前,数据已停)";

        return $"offline(约 {age.TotalDays:0} 天前,历史归档数据)";
    }
}
