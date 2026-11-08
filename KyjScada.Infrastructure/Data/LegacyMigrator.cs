using System.Globalization;
using KyjScada.Domain.History;
using Microsoft.Data.Sqlite;

namespace KyjScada.Infrastructure.Data;

/// <summary>迁移结果。每条都要能对上原文,便于现场核对。</summary>
public sealed class MigrationReport
{
    public string LegacyPath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public int LegacyRows { get; set; }
    public int MigratedRows { get; set; }
    public int Values { get; set; }
    public int DirtySkipped { get; set; }

    /// <summary>
    /// 因"同一变量同一秒已有点"而未插入的个数。
    /// 两个来源:重复执行迁移(应当是全部),或老库本身在同一秒里存了多行。
    /// 必须单独报出来,否则就是"悄悄丢数据"。
    /// </summary>
    public int SkippedExisting { get; set; }

    public int VarCount { get; set; }
    public int SysLogRows { get; set; }
    public bool SysLogTableCreated { get; set; }
    public List<string> Warnings { get; } = new();
    public TimeSpan Elapsed { get; set; }

    public string Describe()
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("老库: " + LegacyPath);
        sb.AppendLine("新库: " + TargetPath);
        sb.AppendLine("宽表数据行: " + LegacyRows + " -> 迁移 " + MigratedRows + " 行");
        sb.AppendLine("有效测点值: " + Values + " 个,涉及变量 " + VarCount + " 个");
        sb.AppendLine("脏值跳过: " + DirtySkipped + " 个(空串/--/非数字)");
        sb.AppendLine("已有点跳过: " + SkippedExisting + " 个(重复执行,或老库同一秒存了多行)");
        sb.AppendLine("系统日志: " + SysLogRows + " 行"
            + (SysLogTableCreated ? " 已存入 LegacySysLog" : " 未迁移"));
        sb.AppendLine("耗时: " + Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " 秒");

        foreach (var w in Warnings) sb.AppendLine("注意: " + w);

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// 老系统宽表 ActualData → 新系统长表 History 的一次性迁移。
///
/// 为什么必须迁移而不是"重新采一段时间就有了":
///  历史数据是资产 —— 客户要看去年同期、要做能效对比、事故调查要倒查。
///  不迁就等于从上线那天起清零。
///
/// 关键取舍:
///  1. **不覆盖新库已有数据**:只用 INSERT,不 DROP 不改表,出问题随时可重跑。
///  2. **脏值一律跳过**:老库空串、" -- "、非数字都很常见,
///     强行 CAST 成 0 会把"没采到"变成"采到了 0",这是最坏的数据污染。
///  3. **系统日志单独存 LegacySysLog**:老 SysLog 混着操作日志/系统报警,
///     直接塞进 AlarmEvents 会把"登录成功"当成报警,语义就毁了。
///  4. **单事务批量插入**:6400 行逐条提交要几十秒,一个事务只要不到一秒。
/// </summary>
public sealed class LegacyMigrator
{
    private static readonly string[] DirtyTokens = { "", "--", "-", "null", "NULL", "NaN" };

    private readonly string _legacyPath;
    private readonly string _targetPath;

    public LegacyMigrator(string legacyPath, string targetPath)
    {
        _legacyPath = legacyPath;
        _targetPath = targetPath;
    }

    /// <summary>老库宽表名。老系统固定叫 ActualData。</summary>
    public string WideTable { get; set; } = "ActualData";

    /// <summary>老库日志表名,不存在就跳过。</summary>
    public string LogTable { get; set; } = "SysLog";

    /// <summary>写入新库时给变量挂的设备名(用于 VarDict 追溯来源)。</summary>
    public string DeviceName { get; set; } = "老系统迁移";

    /// <summary>质量码:迁移进来的历史值一律标"好"(0),因为老系统本身不存质量。</summary>
    public int Quality { get; set; }

    public MigrationReport Run()
    {
        var startedAt = DateTime.Now;
        var report = new MigrationReport { LegacyPath = _legacyPath, TargetPath = _targetPath };

        if (!File.Exists(_legacyPath))
        {
            report.Warnings.Add("老库文件不存在");

            return report;
        }

        var target = new HistoryRepository(_targetPath);

        target.EnsureSchema();

        using var legacy = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _legacyPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());

        legacy.Open();

        var columns = WideColumns(legacy, report);

        if (columns.Count == 0) return report;

        // 变量字典先全部建好,后面插入只用查内存
        var varIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in columns)
        {
            varIds[c] = target.GetOrCreateVarId(c, DeviceName, "");
        }

        report.VarCount = varIds.Count;

        var batch = new List<HistoryRecord>(4096);

        using (var cmd = legacy.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM " + Quote(WideTable);

            using var reader = cmd.ExecuteReader();

            var idxTime = reader.GetOrdinal("InsertTime");
            var idxs = new List<(int Ordinal, int VarId)>(columns.Count);

            foreach (var c in columns)
            {
                idxs.Add((reader.GetOrdinal(c), varIds[c]));
            }

            while (reader.Read())
            {
                report.LegacyRows++;

                var timeText = reader.IsDBNull(idxTime) ? "" : reader.GetString(idxTime);

                if (!TryParseTime(timeText, out var sampleTime))
                {
                    report.Warnings.Add("时间无法解析,整行跳过: " + timeText);

                    continue;
                }

                var rowHasValue = false;

                foreach (var (ordinal, varId) in idxs)
                {
                    if (reader.IsDBNull(ordinal))
                    {
                        report.DirtySkipped++;

                        continue;
                    }

                    var raw = reader.GetValue(ordinal)?.ToString() ?? "";

                    if (!TryParseValue(raw, out var value))
                    {
                        report.DirtySkipped++;

                        continue;
                    }

                    batch.Add(new HistoryRecord(sampleTime, varId, value, Quality));
                    rowHasValue = true;

                    // 分块提交:一次 5 万条以内,既省内存又能看见进度
                    if (batch.Count >= 4096)
                    {
                        WriteChunk(target, batch, report);
                    }
                }

                if (rowHasValue) report.MigratedRows++;
            }
        }

        if (batch.Count > 0) WriteChunk(target, batch, report);

        report.SysLogRows = MigrateSysLog(legacy, report);

        report.Elapsed = DateTime.Now - startedAt;

        return report;
    }

    /// <summary>
    /// 写一块并记账。写进去的和"算是重复跳过"的都要分别累加 ——
    /// 只加写入数会让报表上少掉几百个点却没人知道。
    /// </summary>
    private static void WriteChunk(HistoryRepository target, List<HistoryRecord> batch, MigrationReport report)
    {
        var written = target.WriteBatchIfAbsent(batch);

        report.Values += written;
        report.SkippedExisting += batch.Count - written;

        batch.Clear();
    }

    /// <summary>取宽表的变量列(排除 ID/InsertTime),并校验是否有 InsertTime。</summary>
    private List<string> WideColumns(SqliteConnection legacy, MigrationReport report)
    {
        var all = new List<string>();

        using (var cmd = legacy.CreateCommand())
        {
            cmd.CommandText = "pragma table_info(" + Quote(WideTable) + ")";

            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                all.Add(reader.GetString(1));
            }
        }

        if (all.Count == 0)
        {
            report.Warnings.Add("老库里没有表 " + WideTable);

            return new List<string>();
        }

        if (!all.Contains("InsertTime", StringComparer.OrdinalIgnoreCase))
        {
            report.Warnings.Add("宽表缺少 InsertTime 列,无法确定采样时间");

            return new List<string>();
        }

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ID", "Id", "InsertTime" };

        return all.Where(c => !skip.Contains(c)).ToList();
    }

    /// <summary>
    /// 把老 SysLog 原样搬到新库的 LegacySysLog(保留全部列),
    /// 不做"操作日志 ⇒ 报警"这种语义改写。
    /// </summary>
    private int MigrateSysLog(SqliteConnection legacy, MigrationReport report)
    {
        var cols = new List<string>();

        using (var cmd = legacy.CreateCommand())
        {
            // 表名走参数:LogTable 是 public setter,拼进 SQL 既有注入面,
            // 表名里带单引号时还会直接语法错误。
            cmd.CommandText = "select name from sqlite_master where type='table' and name = $n";
            cmd.Parameters.AddWithValue("$n", LogTable);

            if (cmd.ExecuteScalar() is null)
            {
                report.Warnings.Add("老库没有 " + LogTable + " 表,系统日志未迁移");

                return 0;
            }

            cmd.CommandText = "pragma table_info(" + Quote(LogTable) + ")";

            using var reader = cmd.ExecuteReader();

            while (reader.Read()) cols.Add(reader.GetString(1));
        }

        using var target = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _targetPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString());

        target.Open();

        using (var cmd = target.CreateCommand())
        {
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS LegacySysLog (" +
                string.Join(", ", cols.Select(c => Quote(c) + " TEXT")) +
                ");";

            cmd.ExecuteNonQuery();
        }

        report.SysLogTableCreated = true;

        // 重跑保护:LegacySysLog 没有主键,直接再插一遍会把日志整份翻倍,
        // 而长表/日志表是只增不改的,翻倍之后没法分辨哪份是真的。
        // 整表是在一个事务里写完的 —— 中途失败会整体回滚、表仍为空 ——
        // 所以"表里已经有行"就说明上一次已经迁完,这次直接跳过。
        var existing = CountRows(target, "LegacySysLog");

        if (existing > 0)
        {
            report.Warnings.Add("LegacySysLog 已有 " + existing + " 行,跳过重复迁移(重跑不会翻倍)");

            return 0;
        }

        var written = 0;

        using var tx = target.BeginTransaction();

        using (var read = legacy.CreateCommand())
        {
            read.CommandText = "SELECT * FROM " + Quote(LogTable);

            using var reader = read.ExecuteReader();

            using var ins = target.CreateCommand();

            ins.Transaction = tx;
            // OR IGNORE 是第二道防线:万一将来给 LegacySysLog 加了唯一索引,
            // 重复行也不会让整次迁移在第 3000 行抛异常中断。
            ins.CommandText =
                "INSERT OR IGNORE INTO LegacySysLog (" + string.Join(", ", cols.Select(Quote)) + ") " +
                "VALUES (" + string.Join(", ", cols.Select((_, i) => "$p" + i)) + ")";

            var pars = cols.Select((_, i) => ins.Parameters.Add("$p" + i, SqliteType.Text)).ToList();

            while (reader.Read())
            {
                for (var i = 0; i < cols.Count; i++)
                {
                    pars[i].Value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i)?.ToString() ?? (object)DBNull.Value;
                }

                written += ins.ExecuteNonQuery();
            }
        }

        tx.Commit();

        return written;
    }

    /// <summary>数一张表的行数(表不存在返回 0)。</summary>
    private static long CountRows(SqliteConnection conn, string table)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM " + Quote(table);

            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static bool TryParseValue(string raw, out double value)
    {
        var text = raw.Trim();

        if (DirtyTokens.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            value = 0;

            return false;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;

        // "Infinity"/"-Infinity" 是 double.TryParse 认的合法输入,"1e999" 也会溢出成 Infinity。
        // 这类值一旦写进历史库,趋势图纵轴被拉成无限,聚合出来的平均/最大全是 NaN,
        // 而长表是只增不改的 —— 事后只能删库重来。宁可当脏值跳过并计数。
        if (double.IsNaN(value) || double.IsInfinity(value)) return false;

        return true;
    }

    private static bool TryParseTime(string raw, out DateTime time)
    {
        var formats = new[] { "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff" };

        return DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out time)
               || DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }
}