using KyjScada.Contracts;
using KyjScada.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace KyjScada.Infrastructure.Alarm;

/// <summary>一条报警事件(带 Id 和确认状态,供界面列表用)。</summary>
public sealed record AlarmEventRecord(
    long Id,
    string VarName,
    int Direction,
    bool IsTrigger,
    double Value,
    DateTime At,
    DateTime? AckAt)
{
    /// <summary>方向中文。</summary>
    public string DirectionText => Direction == 0 ? "上限" : "下限";

    /// <summary>事件类型中文。</summary>
    public string TypeText => IsTrigger ? "触发" : "消除";

    /// <summary>是否已确认。</summary>
    public bool IsAcked => AckAt.HasValue;

    /// <summary>确认状态中文。</summary>
    public string AckText => AckAt.HasValue ? "已确认" : "未确认";
}

/// <summary>
/// 报警事件仓储。SQLite 长表,与历史库同库不同表。
///
/// 设计:
///  1. 只存状态转移事件(触发/消除),不存每帧状态 —— 10 年也就几万行。
///  2. 事件带方向和值,历史查询能还原任意时刻的报警画面。
///  3. **带确认状态(AckAt)**:确认是现场必有的动作,
///     没有它就分不清"新报警"和"我已经看过了"。
/// </summary>
public sealed class AlarmRepository
{
    private readonly string _dbPath;

    /// <summary>
    /// 建表 / 切 WAL 的串行锁。journal_mode 是**库级持久设置**,切换要抢独占锁:
    /// 采集线程落报警、界面线程查报警、MCP 的 HTTP 线程查报警会同时首次打开本库,
    /// 不加锁就会撞锁失败(现场表现:"第一次装好时查询报错,重启又好了")。
    /// </summary>
    private static readonly object SchemaLock = new ();

    /// <summary>本库文件的建表状态。跨实例共享:同进程内可能建多个仓储实例。</summary>
    private static readonly HashSet<string> SchemaReady = new (StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// WAL 切换的失败次数与下次重试时刻(按库文件)。
    ///
    /// WAL 与"建表"必须分开记账:
    ///   · 表建好了就是建好了,不需要因为 WAL 没切成就每次重做(建表也有开销);
    ///   · WAL 没切成功要**有限地**重试,不能无限重试 ——
    ///     每次 PRAGMA journal_mode 都要抢库级独占锁,配合 busy_timeout=5000
    ///     每次可能阻塞数秒,把每处 EnsureSchema 变成"每次都卡几秒"。
    /// 所以:失败后按指数退避重试,试满 MaxWalAttempts 次就放弃(只留 LastWalError)。
    /// </summary>
    private static readonly Dictionary<string, (int Attempts, DateTime NextTryUtc)> WalRetry
        = new (StringComparer.OrdinalIgnoreCase);

    private const int MaxWalAttempts = 5;

    /// <summary>
    /// WAL 未能启用的原因,**按库文件**记账(诊断用)。
    ///
    /// 早先是单个静态属性:多库场景下(本轮测试大量临时库、生产换配置目录也是新库)
    /// 库 B 切 WAL 成功就会把它清成 null,把库 A 的失败记录**一并抹掉** ——
    /// 现场"哪个库卡在 delete 模式"再也看不出来,而 B 那次清理本身毫无必要。
    /// </summary>
    private static readonly Dictionary<string, string> WalErrors
        = new (StringComparer.OrdinalIgnoreCase);

    /// <summary>该库最近一次 WAL 失败的原因;该库切成功或从未尝试过则为 null。</summary>
    public static string? GetLastWalError (string dbPath)
    {
        lock (SchemaLock)
        {
            return WalErrors.TryGetValue (dbPath ?? "", out var e) ? e : null;
        }
    }

    /// <summary>
    /// 建表/补列的**真实**失败原因(按库文件记账)。
    ///
    /// 早先补 AckAt 列那一步 catch{} 吞掉一切异常,并无条件标记"建表完成":
    /// 库被锁/只读/磁盘满时,报警确认功能静默全废,而且本进程再也不会重试 ——
    /// 现场看到的是"点确认没反应",一点线索都没有。
    /// </summary>
    private static readonly Dictionary<string, string> SchemaErrors
        = new (StringComparer.OrdinalIgnoreCase);

    /// <summary>该库最近一次建表/补列的失败原因;成功或不曾失败则为 null。</summary>
    public static string? GetLastSchemaError (string dbPath)
    {
        lock (SchemaLock)
        {
            return SchemaErrors.TryGetValue (dbPath ?? "", out var e) ? e : null;
        }
    }

    /// <summary>SQLite 的"列已存在"。只有这一种异常可以当成正常路径。</summary>
    private static bool IsDuplicateColumn (SqliteException ex) =>
        ex.SqliteErrorCode == 1
        && ex.Message.IndexOf ("duplicate column name", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// 该库是否还有"待重试的 WAL 切换"。
    ///
    /// 表建好之后 EnsureSchema 本来可以直接返回,但只要 WAL 还没切成功、
    /// 且没到重试上限,就得再进来一次 —— 否则 TryEnableWal 的退避形同虚设,
    /// 实际只尝试一次,失败就**永久**停在 delete 模式(读写互锁,报警写入开始报 SQLITE_BUSY)。
    /// </summary>
    private static bool WalPending(string dbPath)
    {
        lock (SchemaLock)
        {
            // 退避期没到不算"待办":否则每次 EnsureSchema 都白开一个连接,
            // 进去 TryEnableWal 一看时间不到又直接 return。
            return WalRetry.TryGetValue(dbPath, out var state)
                   && state.Attempts < MaxWalAttempts
                   && DateTime.UtcNow >= state.NextTryUtc;
        }
    }

    public AlarmRepository(string dbPath)
    {
        _dbPath = dbPath;
    }

    private const string CreateSql = @"
CREATE TABLE IF NOT EXISTS AlarmEvents (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    VarName     TEXT    NOT NULL,
    Direction   INTEGER NOT NULL,          -- 0=High 1=Low
    IsTrigger   INTEGER NOT NULL,          -- 1=触发 0=消除
    Value       REAL    NOT NULL,          -- 转移时刻的采样值(消除事件为消除时值)
    OccurredAt  TEXT    NOT NULL,          -- ISO8601 本地时间
    WrittenAt   TEXT    NOT NULL,          -- 落库时刻(诊断用)
    AckAt       TEXT    NULL               -- 确认时刻,NULL=未确认
);
CREATE INDEX IF NOT EXISTS IX_AlarmEvents_VarName_Time
    ON AlarmEvents (VarName, OccurredAt);
CREATE INDEX IF NOT EXISTS IX_AlarmEvents_Time
    ON AlarmEvents (OccurredAt);
";

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // 不用 Shared 缓存:SQLite 官方已列为不推荐,且与 WAL 组合时并发写会直接
            // 返回 SQLITE_LOCKED(历史库那边实测两个线程同时首写就炸)。
            // 并发靠 WAL + busy_timeout 解决,不靠共享页缓存。
            Cache = SqliteCacheMode.Private
        };

        var conn = new SqliteConnection(builder.ToString());
        conn.Open();

        // 每条连接都要:忙等而不是立刻报错。报警在采集线程写、界面/MCP 同时读。
        // **journal_mode 不在这里切** —— 那是库级独占操作,每次开连接都做一遍会让
        // 三方并发互相阻塞数秒;只在 EnsureSchema 里做,切不成按退避重试(见 TryEnableWal)。
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=normal;";
        cmd.ExecuteNonQuery();

        return conn;
    }

    /// <summary>建表(幂等)。表建好之后不再重复;WAL 单独按退避重试。</summary>
    public void EnsureSchema()
    {
        lock (SchemaLock)
        {
            var schemaDone = SchemaReady.Contains(_dbPath);

            // 表已建好但 WAL 还没切成功:必须再进来一次去重试 WAL。
            // 早先这里一律 return,于是 TryEnableWal 的指数退避与 MaxWalAttempts
            // 永远不会被触发 —— 实际上只尝试一次,首次切失败就永久停在 delete 模式。
            if (schemaDone && !WalPending(_dbPath)) return;

            using var conn = OpenConnection();

            TryEnableWal(conn);

            if (schemaDone) return;

            foreach (var sql in new[] { CreateSql })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            // 旧版本建的库没有 AckAt 列。CREATE TABLE IF NOT EXISTS 不会补列,必须显式 ALTER。
            // **只有"列已存在"可以忽略**:其它失败(库被锁/只读/磁盘满)若也吞掉,
            // 就会在确认功能已经全废的情况下标记"建表完成",本进程再也不重试。
            try
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE AlarmEvents ADD COLUMN AckAt TEXT NULL;";
                alter.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (IsDuplicateColumn (ex))
            {
                // 列已存在 —— 正常路径
            }
            catch (Exception ex)
            {
                SchemaErrors[_dbPath] = "报警库补列 AckAt 失败: " + ex.Message;

                // 不标记 SchemaReady:下一轮 EnsureSchema 会再试一次
                return;
            }

            SchemaErrors.Remove (_dbPath);

            // 表已经建好了,这一步不需要因为 WAL 没切成就重做
            SchemaReady.Add(_dbPath);
        }
    }

    /// <summary>
    /// 尝试把库切到 WAL,失败则按指数退避安排下次重试。
    ///
    /// **必须看返回值**:库被别的连接占着时这句会"执行成功但没切过去",
    /// 返回的仍是 delete 模式 —— 读写互锁,报警写入会开始报 SQLITE_BUSY。
    /// 只 try/catch 不看返回值,等于把"没切成"当成"切成了":
    /// 之后 EnsureSchema 直接返回,这个库在本进程里就**永远**停在 delete 模式。
    ///
    /// journal_mode 是库级持久设置,切换要抢独占锁,所以**不能无限重试**
    /// (见 <see cref="WalRetry"/> 的注释)。
    /// </summary>
    private void TryEnableWal(SqliteConnection conn)
    {
        int attempts;

        lock (SchemaLock)
        {
            if (WalRetry.TryGetValue(_dbPath, out var state))
            {
                if (state.Attempts >= MaxWalAttempts) return;

                if (DateTime.UtcNow < state.NextTryUtc) return;

                attempts = state.Attempts;
            }
            else
            {
                attempts = 0;
            }
        }

        string? mode = null;
        string? error = null;

        try
        {
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=wal;";

            // 返回值就是切换后的实际模式:不等于 "wal" 即没切成功
            mode = pragma.ExecuteScalar() as string;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        lock (SchemaLock)
        {
            if (string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                WalRetry.Remove(_dbPath);

                // 只清**本库**的失败记录:别的库还卡在 delete 模式时,
                // 本库切成功不该把它的记录一并抹掉。
                WalErrors.Remove(_dbPath);

                return;
            }

            var next = attempts + 1;

            // 指数退避,封顶 60 秒
            var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, next)));

            WalRetry[_dbPath] = (next, DateTime.UtcNow + delay);

            WalErrors[_dbPath] = "报警库未能启用 WAL(第 " + next + " 次,返回 " + (mode ?? "null") + ")"
                + (error is null ? "" : ":" + error)
                + (next >= MaxWalAttempts
                    ? " —— 已达重试上限,本进程不再尝试(并发写会报 SQLITE_BUSY)"
                    : ",将在 " + (int)delay.TotalSeconds + " 秒后重试");
        }
    }

    /// <summary>写入一批报警事件(单事务)。空批直接返回 0。</summary>
    public int WriteEvents(IEnumerable<AlarmEvent> events)
    {
        var list = events as IReadOnlyCollection<AlarmEvent> ?? events.ToList();
        if (list.Count == 0) return 0;

        EnsureSchema();

        var now = ToDbTime(DateTime.Now);

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
INSERT INTO AlarmEvents (VarName, Direction, IsTrigger, Value, OccurredAt, WrittenAt)
VALUES (@v, @d, @t, @val, @occ, @w);";

            var pV = cmd.CreateParameter(); pV.ParameterName = "@v";
            var pD = cmd.CreateParameter(); pD.ParameterName = "@d";
            var pT = cmd.CreateParameter(); pT.ParameterName = "@t";
            var pVal = cmd.CreateParameter(); pVal.ParameterName = "@val";
            var pOcc = cmd.CreateParameter(); pOcc.ParameterName = "@occ";
            var pW = cmd.CreateParameter(); pW.ParameterName = "@w";

            cmd.Parameters.Add(pV); cmd.Parameters.Add(pD); cmd.Parameters.Add(pT);
            cmd.Parameters.Add(pVal); cmd.Parameters.Add(pOcc); cmd.Parameters.Add(pW);

            foreach (var ev in list)
            {
                pV.Value = ev.VarName;
                pD.Value = (int)ev.Direction;
                pT.Value = ev.IsTrigger ? 1 : 0;
                pVal.Value = ev.Value;
                pOcc.Value = ToDbTime(ev.At);
                pW.Value = now;
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return list.Count;
    }

    /// <summary>查询事件(按时间倒序)。varName 为空查全部。</summary>
    public List<AlarmEventRecord> Query(
        string? varName = null, DateTime? from = null, DateTime? to = null, int limit = 200,
        bool? acked = null)
    {
        // LIMIT 0 是"不返回"而不是"不限",传 0/负数时回落默认条数
        if (limit <= 0) limit = 200;

        EnsureSchema();

        var where = new List<string>();
        if (!string.IsNullOrEmpty(varName)) where.Add("VarName = @v");
        if (from.HasValue) where.Add("OccurredAt >= @f");
        if (to.HasValue) where.Add("OccurredAt <= @t");

        if (acked == true) where.Add("AckAt IS NOT NULL");
        else if (acked == false) where.Add("AckAt IS NULL");

        var sql = "SELECT Id, VarName, Direction, IsTrigger, Value, OccurredAt, AckAt FROM AlarmEvents";
        if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);
        sql += " ORDER BY Id DESC LIMIT @lim;";

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        if (!string.IsNullOrEmpty(varName))
        {
            var p = cmd.CreateParameter(); p.ParameterName = "@v"; p.Value = varName;
            cmd.Parameters.Add(p);
        }
        if (from.HasValue)
        {
            var p = cmd.CreateParameter(); p.ParameterName = "@f"; p.Value = ToDbTime(from.Value);
            cmd.Parameters.Add(p);
        }
        if (to.HasValue)
        {
            var p = cmd.CreateParameter(); p.ParameterName = "@t"; p.Value = ToDbTime(to.Value);
            cmd.Parameters.Add(p);
        }
        var pLim = cmd.CreateParameter(); pLim.ParameterName = "@lim"; pLim.Value = limit;
        cmd.Parameters.Add(pLim);

        var result = new List<AlarmEventRecord>();

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var ackText = reader.IsDBNull(6) ? null : reader.GetString(6);

            DateTime? ack = null;
            if (ackText is not null && DateTime.TryParse(ackText, out var ackAt)) ack = ackAt;

            result.Add(new AlarmEventRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3) == 1,
                reader.GetDouble(4),
                DateTime.Parse(reader.GetString(5)),
                ack));
        }

        return result;
    }

    /// <summary>确认单条事件。</summary>
    public int Acknowledge(long id, DateTime at)
    {
        EnsureSchema();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "UPDATE AlarmEvents SET AckAt = @t WHERE Id = @id AND AckAt IS NULL;";

        cmd.Parameters.AddWithValue("@t", ToDbTime(at));
        cmd.Parameters.AddWithValue("@id", id);

        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 确认某时刻之前的全部未确认事件。返回确认条数。
    /// since 有值时只确认 [since, upto] 区间内的事件 ——
    /// 「全部确认」应该是"确认我看到的这些",不是"确认十年来的所有报警"。
    /// </summary>
    public int AcknowledgeAll(DateTime upto, DateTime at, DateTime? since = null)
    {
        EnsureSchema();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = since.HasValue
            ? "UPDATE AlarmEvents SET AckAt = @t WHERE AckAt IS NULL AND OccurredAt <= @until AND OccurredAt >= @since;"
            : "UPDATE AlarmEvents SET AckAt = @t WHERE AckAt IS NULL AND OccurredAt <= @until;";

        cmd.Parameters.AddWithValue("@t", ToDbTime(at));
        cmd.Parameters.AddWithValue("@until", ToDbTime(upto));

        if (since.HasValue) cmd.Parameters.AddWithValue("@since", ToDbTime(since.Value));

        return cmd.ExecuteNonQuery();
    }

    /// <summary>删除某时刻之前的事件(保留期清理用)。</summary>
    public int DeleteBefore(DateTime time)
    {
        EnsureSchema();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM AlarmEvents WHERE OccurredAt < @t;";
        var p = cmd.CreateParameter(); p.ParameterName = "@t"; p.Value = ToDbTime(time);
        cmd.Parameters.Add(p);

        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 把时间统一成**写入时用的那种文本形状**:`yyyy-MM-ddTHH:mm:ss.fffffff+08:00`。
    ///
    /// OccurredAt 是 TEXT 列,区间过滤走的是**字符串比较**。写库与查询必须用同一种形状,
    /// 否则边界那一行会被静默排除、UTC 时间会让整段区间查不到 —— 细节见 <see cref="DbTime"/>。
    /// </summary>
    private static string ToDbTime (DateTime time) => DbTime.FormatRoundtrip (time);
}
