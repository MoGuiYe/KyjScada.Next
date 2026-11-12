using Microsoft.Data.Sqlite;

namespace KyjScada.Mcp.Data;

/// <summary>
/// 只读打开现场 SQLite 库 —— 双模式:
///   New     检测到 History / VarDict 表(KyjScada.Next 长表结构)
///   Legacy  只有 ActualData / SysLog(老 KYJScadaProWpf 宽表)
/// 优先新库,老库兜底;工具层 API 不变。
///
/// 铁律(来自 07 号报告核实结论,对两种模式同样适用):
///  1. 必须 Mode=ReadOnly —— 主程序用 WAL,读写打开可能干扰 checkpoint 或持锁致主程序 SQLITE_BUSY
///  2. 老库数值列是 varchar(20) 亲和 TEXT → 聚合必须 CAST AS REAL + GLOB 脏行过滤;
///     新库 Value 本身是 REAL NOT NULL,无脏行问题
///  3. NULL 绝不能当 0 返回
///  4. 时间格式严格 yyyy-MM-dd HH:mm:ss(定宽,字典序==时间序)
/// </summary>
public sealed class DbReader
{
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly string _connectionString;
    private readonly bool _isNew;

    public DbReader(string dbPath)
    {
        // Mode=ReadOnly 是硬要求,不要改
        _connectionString = $"Data Source={dbPath};Mode=ReadOnly;Pooling=true;Default Timeout=5";
        _alarmDbPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".", "alarms.db");
        _isNew = DetectNewSchema();
    }

    /// <summary>
    /// 新架构把报警单独存在同目录的 alarms.db(主程序 AlarmRepository 就写这个文件),
    /// 历史库 history.db 里没有 AlarmEvents。只认 history.db 会让 query_alarms 永远返回空,
    /// 所以这里把同目录的 alarms.db 也当成报警来源。
    /// </summary>
    private readonly string _alarmDbPath;

    private string? _alarmConnectionString;

    /// <summary>true = 新长表(History/VarDict);false = 老宽表(ActualData)。</summary>
    public bool IsNewSchema => _isNew;

    public string DatabasePath => _connectionString;

    private bool DetectNewSchema()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('History','VarDict')";
            return Convert.ToInt64(cmd.ExecuteScalar()) >= 2;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>库是否存在且可打开。</summary>
    public bool CanOpen()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ==================== 变量列(元数据) ====================

    /// <summary>
    /// 新库变量字典:VarName -> (DeviceName, Unit)。老库返回空字典。
    /// 供 list_variables 展示设备/单位,无需解析组态 JSON。
    /// </summary>
    public Dictionary<string, (string? Device, string? Unit)> GetVarDict()
    {
        var dict = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        if (!_isNew) return dict;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT VarName, DeviceName, Unit FROM VarDict ORDER BY VarId";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            dict[reader.GetString(0)] = (
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2));
        }
        return dict;
    }

    /// <summary>全部数据变量名。新库取 VarDict.VarName,老库取 ActualData 列。</summary>
    public List<string> GetDataColumns()
    {
        var cols = new List<string>();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        if (_isNew)
        {
            cmd.CommandText = "SELECT VarName FROM VarDict ORDER BY VarId";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) cols.Add(reader.GetString(0));
        }
        else
        {
            cmd.CommandText = "PRAGMA table_info(\"ActualData\")";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                if (name.Equals("ID", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("InsertTime", StringComparison.OrdinalIgnoreCase)) continue;
                cols.Add(name);
            }
        }

        return cols;
    }

    // ==================== 最新快照 ====================

    /// <summary>
    /// 读最新值快照。新库:每个变量取最近一条(长表按 VarId 取 MAX(Id))。
    /// 老库:整行 ActualData。返回 字典: 变量名 -> 原始文本值(可能 null)。
    /// </summary>
    public (string? InsertTime, Dictionary<string, string?> Values, Dictionary<string, string?> Times)? ReadLatestRow()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // 每个变量各自的时间戳:原来只返回"所有变量里最新的那一个",于是
        // 一个半天没更新的点也会被报成 fresh —— 现场据此判断"设备正常",
        // 实际那个点早就掉线了。
        var times = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        string? latestTime = null;

        if (_isNew)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT d.VarName, h.Value, h.SampleTime FROM History h " +
                "JOIN VarDict d ON d.VarId = h.VarId " +
                "WHERE h.Id IN (SELECT MAX(Id) FROM History GROUP BY VarId)";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                var value = reader.IsDBNull(1) ? null : reader.GetDouble(1).ToString("0.######");
                var time = reader.IsDBNull(2) ? null : reader.GetString(2);
                values[name] = value;
                times[name] = time;
                if (time != null && (latestTime == null || string.CompareOrdinal(time, latestTime) > 0))
                    latestTime = time;
            }
        }
        else
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM ActualData ORDER BY ID DESC LIMIT 1";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            latestTime = reader["InsertTime"] is DBNull ? null : Convert.ToString(reader["InsertTime"]);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                if (name.Equals("ID", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("InsertTime", StringComparison.OrdinalIgnoreCase)) continue;
                values[name] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i));

                // 宽表的一行本来就是同一时刻,逐列沿用行时间
                times[name] = latestTime;
            }
        }

        if (values.Count == 0) return null;
        return (latestTime, values, times);
    }

    // ==================== 历史时序 ====================

    /// <summary>
    /// 查询历史时序。变量名由调用方给定(必须是已知变量,防注入)。
    /// 返回按时间升序的行;同一时刻多变量的值合并进同一行(缺失变量为 null)。
    /// </summary>
    public List<HistoryRow> QueryHistory(
        IEnumerable<string> columns,
        DateTime start,
        DateTime end,
        int limit = 2000)
    {
        var known = GetDataColumns().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var safe = columns.Where(known.Contains).ToList();
        var rows = new List<HistoryRow>();
        if (safe.Count == 0) return rows;

        // LIMIT 0 在 SQLite 里返回 0 行(不是"不限"),AI 客户端传 limit=0 会得到空结果,
        // 看起来像"这个变量没有历史数据" —— 静默误导,不如给个默认值。
        if (limit <= 0) limit = 2000;

        // 上限不只是"礼貌":AI 客户端真会传 limit=100000,而下面要取
        // limit × 变量数 行再在内存里按时刻合并 —— 不封顶就是把整个库读进进程。
        if (limit > 2000) limit = 2000;

        // 合并用的行数上限。取 2000 点 × 20 个变量 = 4 万行已经够拼出 2000 个时刻,
        // 再多也只会被 Take(limit) 丢掉。
        const int MaxFetchRows = 500000;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        if (_isNew)
        {
            var names = string.Join(", ", safe.Select(c => "'" + c.Replace("'", "''") + "'"));
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT h.SampleTime, d.VarName, h.Value FROM History h " +
                "JOIN VarDict d ON d.VarId = h.VarId " +
                "WHERE d.VarName COLLATE NOCASE IN (" + names + ") " +
                "AND h.SampleTime >= @s AND h.SampleTime <= @e " +
                // 必须 DESC:ASC + LIMIT 拿到的是时间窗里**最早**的那批,
                // "查最近 2 小时"实际返回的是两小时之前头几十条 —— 数据没错但全错位。
                // 取完再翻回升序(接口承诺升序),见下面的 Skip。
                "ORDER BY h.SampleTime DESC LIMIT @n";
            cmd.Parameters.AddWithValue("@s", DbText(start, TimeFormat));
            cmd.Parameters.AddWithValue("@e", DbText(end, TimeFormat));

            // 用 long 算再夹住:limit * safe.Count 在 int 上会静默溢出成负数,
            // SQLite 的 LIMIT 负数 = 不限,恰好是最危险的结果。
            var fetch = (long)limit * safe.Count;
            if (fetch > MaxFetchRows) fetch = MaxFetchRows;
            cmd.Parameters.AddWithValue("@n", (int)fetch);

            var byTime = new SortedDictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var time = reader.GetString(0);
                var varName = reader.GetString(1);
                var value = reader.IsDBNull(2) ? null : reader.GetDouble(2).ToString("0.######");
                if (!byTime.TryGetValue(time, out var dict))
                    byTime[time] = dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                dict[varName] = value;
            }

            // 只保留**最近**的 limit 个时刻(取排序后的尾部),输出仍是升序:
            // 原来 Take(limit) 取的是头部 = 最早的一批,与"最近一次查询"的意图正好相反。
            var skip = Math.Max(0, byTime.Count - limit);

            foreach (var kv in byTime.Skip(skip))
                rows.Add(new HistoryRow(kv.Key, kv.Value));
        }
        else
        {
            var quoted = string.Join(", ", safe.Select(c => "\"" + c.Replace("\"", "\"\"") + "\""));
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT InsertTime, " + quoted + " FROM ActualData " +
                "WHERE InsertTime >= @s AND InsertTime <= @e " +
                // 同上:DESC 取最近的 limit 条,再翻回升序
                "ORDER BY InsertTime DESC LIMIT @n";
            cmd.Parameters.AddWithValue("@s", DbText(start, TimeFormat));
            cmd.Parameters.AddWithValue("@e", DbText(end, TimeFormat));
            cmd.Parameters.AddWithValue("@n", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var time = Convert.ToString(reader["InsertTime"]) ?? "";
                var vals = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < safe.Count; i++)
                    vals[safe[i]] = reader.IsDBNull(i + 1) ? null : Convert.ToString(reader.GetValue(i + 1));
                rows.Add(new HistoryRow(time, vals));
            }

            // SQL 是 DESC(取最近的那批),接口承诺升序 —— 在这里翻回来
            rows.Reverse();
        }

        return rows;
    }

    // ==================== 统计聚合 ====================

    /// <summary>
    /// 统计聚合。老库必须 CAST AS REAL + GLOB 脏行过滤;新库 Value 是 REAL,直接聚合。
    /// </summary>
    public StatResult? QueryStat(string column, DateTime start, DateTime end)
    {
        var known = GetDataColumns().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!known.Contains(column)) return null;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        if (_isNew)
        {
            cmd.CommandText =
                "SELECT COUNT(*), MIN(h.Value), MAX(h.Value), AVG(h.Value) FROM History h " +
                "JOIN VarDict d ON d.VarId = h.VarId " +
                "WHERE d.VarName = @v COLLATE NOCASE AND h.SampleTime >= @s AND h.SampleTime <= @e";
            cmd.Parameters.AddWithValue("@v", column);
        }
        else
        {
            var q = "\"" + column.Replace("\"", "\"\"") + "\"";

            // 只让"真能当数字看"的行参与聚合。
            // 原来只有 GLOB '[0-9-]*'(首字符是数字或减号),于是 '--'、'12abc'、' 5'、
            // '1.2.3' 这些老库里真实存在的脏值全部混进 CAST: '--' 变 0、'12abc' 变 12,
            // MIN/MAX/AVG 就此失真,而工具照样报"统计正常"。
            //
            // 正负号规则要认得**指数**:老库里有 '-1.5e-3'、'1E+5' 这类合法写法,
            // 早先一句 NOT GLOB '*[-+]*[-+]*' 把凡是带两个符号的都当脏值,
            // 负指数整类被漏掉(统计少算);而 '1e999'、'5-' 又能混进去(CAST 成 Inf/截断)。
            // 去掉指数里的正负号,只留"数字/小数点/eE"和可能的前导符号,
            // 用来判断符号是否出现在非法位置(见下面的说明)。
            var s = "REPLACE(REPLACE(REPLACE(REPLACE(" + q
                + ", 'e-', ''), 'E-', ''), 'e+', ''), 'E+', '')";

            var strict =
                " AND TRIM(" + q + ") = " + q +                                   // 不许首尾空格
                " AND " + q + " NOT GLOB '*[^0-9eE+.-]*'" +                        // 只允许数字/指数/正负号/小数点
                " AND " + q + " GLOB '*[0-9]*'" +                                 // 至少一个数字(挡 '--'、'-'、'')
                " AND " + q + " GLOB '*[0-9]'" +                                  // 结尾必须是数字(挡 '5-'、'1.5e-')
                " AND " + q + " NOT GLOB '*[.]*[.]*'" +                            // 不许两个小数点
                " AND " + q + " NOT GLOB '*[eE]*[eE]*'" +                          // 不许两个指数符号
                // 正负号**只能出现在开头,或紧跟 e/E**(指数符号)。
                // 早先只数"符号个数不超过两个",于是 '1+2'、'1-2' 这种也放过 ——
                // CAST('1+2' AS REAL) 在 SQLite 里取到 1.0,这类脏值会静默混进
                // MIN/MAX/AVG,统计看着正常其实全是假的。
                //
                // GLOB 做不到"看前一个字符",所以先把指数符号剥掉再判:
                //   s = 去掉 'e-'/'E-'/'e+'/'E+' 之后的串
                //   合法 ⟺ s 里没有符号,或者只有开头一个符号
                " AND ((" + s + " NOT GLOB '*[-+]*')" +
                "      OR (" + s + " GLOB '[+-]*'" +
                "          AND SUBSTR(" + s + ", 2) NOT GLOB '*[-+]*'))" +
                " AND ABS(CAST(" + q + " AS REAL)) <= 1e308";                      // 挡 '1e999' 这类溢出成 Inf 的

            cmd.CommandText =
                "SELECT COUNT(*), MIN(CAST(" + q + " AS REAL)), MAX(CAST(" + q + " AS REAL)), " +
                "AVG(CAST(" + q + " AS REAL)) FROM ActualData " +
                "WHERE InsertTime >= @s AND InsertTime <= @e" + strict;
        }

        cmd.Parameters.AddWithValue("@s", DbText(start, TimeFormat));
        cmd.Parameters.AddWithValue("@e", DbText(end, TimeFormat));

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var count = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
        var min = reader.IsDBNull(1) ? (double?)null : reader.GetDouble(1);
        var max = reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2);
        var avg = reader.IsDBNull(3) ? (double?)null : reader.GetDouble(3);

        return new StatResult(count, min, max, avg);
    }

    // ==================== 报警 / 日志 ====================

    /// <summary>
    /// 查询报警与日志。新库读 AlarmEvents(触发/消除映射为类型);
    /// 老库读 SysLog(LogType 实测三类:系统日志 / 操作日志 / 系统报警)。
    /// </summary>
    public List<LogRow> QueryLogs(DateTime start, DateTime end, string? logType = null, int limit = 500)
    {
        return QueryLogs(start, end, out _, logType, limit);
    }

    /// <summary>
    /// 同上,并把读取失败的原因带出来。
    ///
    /// 为什么必须带 error:底层 catch 住异常只返回"已读到的部分",
    /// 调用方看到的是一张空表 —— AI 会把它当成"当前没有报警",
    /// 而不是"报警库读不出来"。这两种结论对现场的含义完全相反:
    /// 前者可以放心继续查,后者必须先去修库。
    /// </summary>
    public List<LogRow> QueryLogs(
        DateTime start, DateTime end, out string? error, string? logType = null, int limit = 500)
    {
        var rows = new List<LogRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();

        if (_isNew)
        {
            // 两处都读:历史库(老的新版迁移库可能带 AlarmEvents) + 同目录 alarms.db(现行写法)
            var fromHistory = ReadAlarmEvents(_connectionString, start, end, limit, out var e1);
            var sidecar = SidecarAlarmConnectionString();

            string? e2 = null;

            var fromAlarmDb = sidecar == null
                ? new List<LogRow>()
                : ReadAlarmEvents(sidecar, start, end, limit, out e2);

            if (e1 is not null) errors.Add("历史库: " + e1);
            if (e2 is not null) errors.Add("报警库: " + e2);

            foreach (var row in fromHistory.Concat(fromAlarmDb))
            {
                // 去重键必须带**类型(触发/消除)**与**值**。
                // 早先只有 "时间|变量|方向",而 InsertTime 粒度只到秒 ——
                // 同一秒里触发了高限、又触发了另一个值的点,后一条会被当成重复丢掉。
                var key = row.InsertTime + "|" + row.VarName + "|" + row.AlarmType
                    + "|" + row.LogType + "|" + row.AlarmValue + "|" + row.Note;

                if (!seen.Add(key)) continue;

                rows.Add(row);
            }

            rows.Sort((a, b) => string.CompareOrdinal(b.InsertTime, a.InsertTime));
            if (rows.Count > limit) rows.RemoveRange(limit, rows.Count - limit);
        }
        else
        {
            // 连接**只在这一支里开**:新库分支根本不碰主库(它读历史库 + alarms.db)。
            // 早先无条件 conn.Open() 主库,主库被锁/损坏时这一句就抛,
            // 报警一条都读不出来 —— 而这正是最需要看报警的时候。
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Id, InsertTime, LogType, Operator, Note, varName, AlarmSet, AlarmValue, AlarmType " +
                "FROM SysLog WHERE InsertTime >= @s AND InsertTime <= @e " +
                (logType != null ? "AND LogType = @t " : "") +
                "ORDER BY Id DESC LIMIT @n";
            cmd.Parameters.AddWithValue("@s", DbText(start, TimeFormat));
            cmd.Parameters.AddWithValue("@e", DbText(end, TimeFormat));
            cmd.Parameters.AddWithValue("@n", limit);
            if (logType != null) cmd.Parameters.AddWithValue("@t", logType);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new LogRow(
                    reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                    reader.IsDBNull(1) ? "" : Convert.ToString(reader.GetValue(1)) ?? "",
                    reader.IsDBNull(2) ? "" : Convert.ToString(reader.GetValue(2)) ?? "",
                    reader.IsDBNull(3) ? "" : Convert.ToString(reader.GetValue(3)) ?? "",
                    reader.IsDBNull(4) ? "" : Convert.ToString(reader.GetValue(4)) ?? "",
                    reader.IsDBNull(5) ? null : Convert.ToString(reader.GetValue(5)),
                    reader.IsDBNull(6) ? null : Convert.ToString(reader.GetValue(6)),
                    reader.IsDBNull(7) ? null : Convert.ToString(reader.GetValue(7)),
                    reader.IsDBNull(8) ? null : Convert.ToString(reader.GetValue(8))));
            }
        }

        error = errors.Count == 0 ? null : string.Join("; ", errors);

        return rows;
    }

    /// <summary>同目录 alarms.db 的只读连接串;文件不存在返回 null。</summary>
    private string? SidecarAlarmConnectionString()
    {
        if (_alarmConnectionString != null) return _alarmConnectionString;
        try
        {
            if (!System.IO.File.Exists(_alarmDbPath)) return null;
            _alarmConnectionString = $"Data Source={_alarmDbPath};Mode=ReadOnly;Pooling=true;Default Timeout=5";
            return _alarmConnectionString;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把查询时间换算成**库里那种文本**再拿去比较。库内时间是 TEXT 列,区间过滤靠字符串比较,
    /// 因此写库与查询必须用同一种形状,否则会出现两类只能现场复现的怪事:
    ///  1. 传 Unspecified(Kind 未知)的时间:`ToString("o")` **不带** +08:00,
    ///     它恰好是带偏移文本的前缀,"同一毫秒那一行"就被排除在区间外;
    ///  2. 传 UTC 时间:输出带 "Z",而库内是 "+08:00" —— 'Z'(0x5A) 恒大于 '+'(0x2B),
    ///     整段区间查不到东西。
    /// 这里先换算成本地时间,再用固定格式输出。
    ///
    /// 注意:MCP 是独立程序集(不引用主程序集,自带模型),所以这段与
    /// KyjScada.Infrastructure.Data.DbTime 同源但不共享 —— 改这里请顺手看一眼那边。
    /// </summary>
    private static string DbText(DateTime time, string format)
    {
        var local = time.Kind switch
        {
            DateTimeKind.Utc => time.ToLocalTime(),
            DateTimeKind.Local => time,
            _ => DateTime.SpecifyKind(time, DateTimeKind.Local)
        };

        return local.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// AlarmEvents.OccurredAt 是 "o"(ISO8601)格式,直接给人看太丑
    /// (2026-11-06T13:19:45.6525797+08:00)。统一转成库里的标准时间格式再输出。
    /// </summary>
    private static string NormalizeTime(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
        {
            return dt.ToString(TimeFormat, System.Globalization.CultureInfo.InvariantCulture);
        }
        return raw;
    }

    /// <summary>从指定库里读 AlarmEvents;表不存在返回空表(不抛)。</summary>
    private static List<LogRow> ReadAlarmEvents(
        string connectionString, DateTime start, DateTime end, int limit, out string? error)
    {
        var rows = new List<LogRow>();
        error = null;

        try
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            using (var chk = conn.CreateCommand())
            {
                chk.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='AlarmEvents'";
                if (Convert.ToInt64(chk.ExecuteScalar()) == 0) return rows;
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Id, OccurredAt, Direction, IsTrigger, VarName, Value, AckAt " +
                "FROM AlarmEvents WHERE OccurredAt >= @s AND OccurredAt <= @e " +
                "ORDER BY Id DESC LIMIT @n";
            // AlarmEvents.OccurredAt 落库用 "o"(ISO8601,带 T 与小数秒),
            // 必须用同格式比较;用空格格式在同一天内会因 ' '(0x20) < 'T' 误判边界。
            cmd.Parameters.AddWithValue("@s", DbText(start, "o"));
            cmd.Parameters.AddWithValue("@e", DbText(end, "o"));
            cmd.Parameters.AddWithValue("@n", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var dir = reader.GetInt64(2) == 0 ? "高限" : "低限";
                var kind = reader.GetInt64(3) == 1 ? "报警" : "消除";
                var ack = reader.IsDBNull(6) ? null : reader.GetString(6);
                rows.Add(new LogRow(
                    reader.GetInt64(0),
                    NormalizeTime(reader.GetString(1)),
                    kind,
                    ack == null ? "" : "已确认",
                    dir + " " + kind + (ack == null ? "" : "(确认于 " + NormalizeTime(ack) + ")"),
                    reader.GetString(4),
                    null,
                    reader.IsDBNull(5) ? null : reader.GetDouble(5).ToString("0.######"),
                    dir));
            }
        }
        catch (Exception ex)
        {
            // 库被独占锁住/文件损坏时不要炸掉整个工具调用,返回已读到的部分。
            // 但**必须记下原因**:否则上层只看到空表,把这当成"没有报警"。
            error = ex.GetBaseException().Message;
        }

        return rows;
    }

    // ==================== 库概况 ====================

    /// <summary>库概况:行数、时间跨度、表清单、模式。</summary>
    public DbSummary GetSummary()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        long rows = 0;
        string? minTime = null;
        string? maxTime = null;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = _isNew
                ? "SELECT COUNT(*), MIN(SampleTime), MAX(SampleTime) FROM History"
                : "SELECT COUNT(*), MIN(InsertTime), MAX(InsertTime) FROM ActualData";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                rows = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                minTime = reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1));
                maxTime = reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2));
            }
        }

        long logRows = 0;

        if (_isNew)
        {
            // 报警行数 = 历史库里的 AlarmEvents(若有) + 同目录 alarms.db(现行)
            logRows += CountAlarmEvents(_connectionString);
            var sidecar = SidecarAlarmConnectionString();
            if (sidecar != null) logRows += CountAlarmEvents(sidecar);
        }
        else
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM SysLog";
            var obj = cmd.ExecuteScalar();
            if (obj != null && obj != DBNull.Value) logRows = Convert.ToInt64(obj);
        }

        return new DbSummary(rows, minTime, maxTime, logRows, GetDataColumns(), _isNew ? "new(History 长表)" : "legacy(ActualData 宽表)");
    }

    /// <summary>某库里 AlarmEvents 的行数;表不存在/开不了返回 0。</summary>
    private static long CountAlarmEvents(string connectionString)
    {
        try
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();
            using var chk = conn.CreateCommand();
            chk.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='AlarmEvents'";
            if (Convert.ToInt64(chk.ExecuteScalar()) == 0) return 0;
            using var cnt = conn.CreateCommand();
            cnt.CommandText = "SELECT COUNT(*) FROM AlarmEvents";
            var obj = cnt.ExecuteScalar();
            return obj == null || obj == DBNull.Value ? 0 : Convert.ToInt64(obj);
        }
        catch
        {
            return 0;
        }
    }
}

public sealed record HistoryRow(string InsertTime, Dictionary<string, string?> Values);

public sealed record StatResult(long Count, double? Min, double? Max, double? Avg);

public sealed record LogRow(
    long Id, string InsertTime, string LogType, string Operator, string Note,
    string? VarName, string? AlarmSet, string? AlarmValue, string? AlarmType);

public sealed record DbSummary(
    long ActualDataRows, string? MinTime, string? MaxTime, long SysLogRows,
    List<string> DataColumns, string Schema = "legacy(ActualData 宽表)");
