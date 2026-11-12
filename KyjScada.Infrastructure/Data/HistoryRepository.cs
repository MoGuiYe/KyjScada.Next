using System.Globalization;
using KyjScada.Contracts;
using KyjScada.Domain.History;
using Microsoft.Data.Sqlite;

namespace KyjScada.Infrastructure.Data;

/// <summary>
/// 历史数据仓储。长表实现。
///
/// 关键设计:
///  1. **只存有效值**。无效值不写行,不写 NULL。
///     老库 LQB1_Start 96% 是 NULL,那是宽表的必然浪费;
///     长表不需要为不存在的值留位置。
///  2. **批量写入单事务**。每 10 秒一批,一个事务提交,
///     避免 35 次独立提交(每次都有 fsync 开销)。
///  3. **VarId 缓存**。变量名到 ID 的映射在内存里缓存,
///     避免每次写都查一次 VarDict。
/// </summary>
public sealed class HistoryRepository : IHistoryRepository
{
    /// <summary>
    /// 契约实现:按变量名归档一批点。内部解析 VarId 后走既有 WriteBatch。
    /// </summary>
    public int WritePoints(IReadOnlyList<HistoryPoint> points)
    {
        if (points.Count == 0) return 0;

        // 先把"缓存里还没有"的变量名**去重凑成一批**,用一个连接一次解决。
        // 早先是逐点调 GetOrCreateVarId:首次归档 35 个变量就是 35 次 OpenConnection
        // + 35 次 INSERT/SELECT,首个归档周期明显拖慢(库在机械盘/网络盘上更明显)。
        var missing = new List<(string Name, string Device, string Unit)>();

        lock (_cacheLock)
        {
            foreach (var p in points)
            {
                if (_varIdCache.ContainsKey(p.VarName)) continue;

                var already = false;

                foreach (var m in missing)
                {
                    if (string.Equals(m.Name, p.VarName, StringComparison.OrdinalIgnoreCase))
                    {
                        already = true;

                        break;
                    }
                }

                if (!already) missing.Add((p.VarName, p.DeviceName, p.Unit));
            }
        }

        if (missing.Count > 0)
        {
            EnsureSchema();

            using var conn = OpenConnection();

            foreach (var m in missing) GetOrCreateVarIdCore(conn, m.Name, m.Device, m.Unit);
        }

        var records = new List<HistoryRecord>(points.Count);

        lock (_cacheLock)
        {
            foreach (var p in points)
            {
                if (!_varIdCache.TryGetValue(p.VarName, out var id))
                {
                    // 上方已保证建齐;走到这里说明缓存被并发清掉了,宁可报错也不能写错 VarId
                    throw new InvalidOperationException("取建变量 ID 失败: " + p.VarName);
                }

                records.Add(new HistoryRecord(p.SampleTime, id, p.Value, p.Quality));
            }
        }

        return WriteBatch(records);
    }

    private readonly string _dbPath;
    private readonly Dictionary<string, int> _varIdCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();

    /// <summary>建表开关。跨实例共享:同一个库文件在进程内可能被多个仓储实例打开。</summary>
    private static readonly HashSet<string> SchemaReady = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// WAL 切换的失败次数与下次重试时刻(按库文件)。
    ///
    /// WAL 与"建表"必须分开记账:
    ///   · 表建好了就是建好了,不需要因为 WAL 没切成就每次重做(建表也有开销);
    ///   · WAL 没切成功要**有限地**重试,不能无限重试 ——
    ///     每次 PRAGMA journal_mode 都要抢库级独占锁,配合 busy_timeout=5000
    ///     每次可能阻塞数秒,把八处 EnsureSchema 变成"每次都卡几秒"。
    /// 所以:失败后按指数退避重试,试满 MaxWalAttempts 次就放弃(只留 LastWalError)。
    /// </summary>
    private static readonly Dictionary<string, (int Attempts, DateTime NextTryUtc)> WalRetry
        = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxWalAttempts = 5;

    /// <summary>
    /// 建索引的重试记账(按库文件)。与 <see cref="WalRetry"/> 同一个道理:
    ///   · 索引失败**不能就此定案** —— 建不全就只剩"重启一次机会"了,
    ///     而缺的偏偏是 idx_history_var_id 这种"每变量最新值"要用的索引;
    ///   · 也不能每次 EnsureSchema 都重试一遍 ——
    ///     在几十万行的库上 CREATE INDEX 是一次真实写操作(要排序、依赖临时空间),
    ///     失败一次可能好几秒,而 EnsureSchema 是**每次归档写**都会走的。
    /// 所以:失败后按指数退避重试,试满 MaxIndexAttempts 次就放弃(只留 LastIndexError)。
    /// </summary>
    private static readonly Dictionary<string, (int Attempts, DateTime NextTryUtc)> IndexRetry
        = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxIndexAttempts = 5;

    /// <summary>
    /// 该库是否还有"待重试的建索引"。
    ///
    /// 表建好之后 EnsureSchema 本来可以直接返回,但只要索引还没建全、
    /// 且没到重试上限,就得再进来一次 —— 否则退避形同虚设,
    /// 实际只尝试一次,本进程内再也不建索引(查询静默退化成全表扫描)。
    /// </summary>
    private static bool IndexPending(string dbPath)
    {
        lock (SchemaLock)
        {
            // **没到退避时刻也算"没有待办"**:否则退避期里的每一次 EnsureSchema
            // (每次归档写都会走)都会白开一个连接进去,TryCreateIndexes 一看时间不到
            // 又直接 return —— 退避最长 60 秒,期间白开的连接全是无谓开销。
            return IndexRetry.TryGetValue(dbPath, out var state)
                   && state.Attempts < MaxIndexAttempts
                   && DateTime.UtcNow >= state.NextTryUtc;
        }
    }

    /// <summary>
    /// WAL 未能启用的原因,**按库文件**记账(诊断用)。
    ///
    /// 早先是单个静态属性:多库场景下(本轮测试大量临时库、生产换配置目录也是新库)
    /// 库 B 切 WAL 成功就会把它清成 null,把库 A 的失败记录**一并抹掉** ——
    /// 现场"哪个库卡在 delete 模式"再也看不出来,而 B 那次清理本身毫无必要。
    /// 现在与 <see cref="WalRetry"/> 同一口径:只清/只记本库的。
    /// </summary>
    private static readonly Dictionary<string, string> WalErrors
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 建索引未能完成的原因,**按库文件**记账(诊断用)。
    ///
    /// 同样是 static 全局的老问题:库 B 建索引成功会把它清成 null,
    /// 于是 <see cref="IndexPending"/> 判断"该库还有没有索引没建全"的记账
    /// 与实际错误对不上 —— 库 A 的失败提示被库 B 的成功悄悄抹掉。
    /// 索引只影响查询快慢,不影响正确性,所以这里**只记录不抛**;
    /// 界面把它显示出来,现场才知道"最近值查询可能偏慢"是有原因的。
    /// </summary>
    private static readonly Dictionary<string, string> IndexErrors
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>该库最近一次 WAL 失败的原因;该库切成功或从未尝试过则为 null。</summary>
    public static string? GetLastWalError(string dbPath)
    {
        lock (SchemaLock)
        {
            return WalErrors.TryGetValue(dbPath ?? "", out var e) ? e : null;
        }
    }

    /// <summary>该库最近一次建索引失败的原因;该库建全或从未尝试过则为 null。</summary>
    public static string? GetLastIndexError(string dbPath)
    {
        lock (SchemaLock)
        {
            return IndexErrors.TryGetValue(dbPath ?? "", out var e) ? e : null;
        }
    }

    /// <summary>
    /// 该库是否还有"待重试的 WAL 切换"。
    ///
    /// 表建好之后 EnsureSchema 本来可以直接返回,但只要 WAL 还没切成功、
    /// 且没到重试上限,就得再进来一次 —— 否则 TryEnableWal 的退避形同虚设,
    /// 实际只尝试一次,失败就永远停在 delete 模式(读写互锁)。
    /// </summary>
    private static bool WalPending(string dbPath)
    {
        lock (SchemaLock)
        {
            // 与 IndexPending 同理:退避期没到就不算"待办",
            // 免得每次归档写都白开一个连接、进去立刻又 return。
            return WalRetry.TryGetValue(dbPath, out var state)
                   && state.Attempts < MaxWalAttempts
                   && DateTime.UtcNow >= state.NextTryUtc;
        }
    }

    /// <summary>
    /// 建表/改 journal_mode 的串行锁。
    ///
    /// 必须按库文件串行:journal_mode 是**库级持久设置**,切换时要求独占锁,
    /// 两个线程(归档线程 + MCP/报表)同时对一个新库切 WAL,后到的那个会撞锁失败 ——
    /// 现场表现是"程序刚装好第一次打开数据库时 MCP 报错,重启又好了"。
    /// </summary>
    private static readonly object SchemaLock = new();

    public HistoryRepository(string dbPath)
    {
        _dbPath = dbPath;
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // 不用 Shared 缓存:SQLite 官方已把它列为不推荐,且与 WAL 组合时
            // 并发写会直接返回 SQLITE_LOCKED(实测两个线程同时首写就炸)。
            // 并发靠 WAL + busy_timeout 解决,不靠共享页缓存。
            Cache = SqliteCacheMode.Private
        };

        var conn = new SqliteConnection(builder.ToString());
        conn.Open();

        // 每条连接都要的:忙等而不是立刻报错。
        // 归档线程在写、界面/MCP 同时在读,没这个设置时"库被占用"会直接抛给上层。
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=normal;";
        cmd.ExecuteNonQuery();

        return conn;
    }

    /// <summary>建表(幂等)。表建好之后不再重复;WAL 与建索引各自按退避重试。</summary>
    public void EnsureSchema()
    {
        lock (SchemaLock)
        {
            var schemaDone = SchemaReady.Contains(_dbPath);

            // 只要还有一件没做完就得再进来:表没建 / WAL 没切成功 / 索引没建全。
            // 早先"表建好就一律 return",于是 WAL 的退避与索引的重试都形同虚设。
            if (schemaDone && !WalPending(_dbPath) && !IndexPending(_dbPath)) return;

            using var conn = OpenConnection();

            TryEnableWal(conn);

            if (!schemaDone)
            {
                // 建表:失败就是致命(没有表既不能归档也不能查询),抛出去并带上具体语句。
                ExecuteStatements(conn, HistorySchema.CreateTableStatements, fatal: true);

                // 表已经建好了,这一步不需要因为 WAL/索引没成而重做
                SchemaReady.Add(_dbPath);
            }

            if (!schemaDone || IndexPending(_dbPath)) TryCreateIndexes(conn);
        }
    }

    /// <summary>
    /// 建索引。**失败绝不能致命,也绝不能一条失败就把后面的全丢掉**。
    ///
    /// 索引只是性能优化,而在已有大库上建索引是一次真实写操作(CREATE INDEX 要排序,
    /// 依赖临时空间):实测在 app 自身进程里对 29 万行的现场库建 idx_history_var_id
    /// 会报 SQLITE_CANTOPEN(error 14),而同一份库在全新进程里能建成功。
    /// 若让它抛,整个历史库会连**读**都做不了 —— 把"慢一点"变成"完全不能用"。
    ///
    /// 三条索引的顺序是"时间 → 变量+时间 → 变量+Id":越靠后越是"每变量最新值"
    /// (MCP read_variable / 界面最新值)的关键索引。早先这里是"第一条失败就 return",
    /// 于是第一条一挂,后面两条**一条都不建** —— 偏偏挂的常常是第一条,
    /// 结果把最要命的 idx_history_var_id 一起丢了,查询静默退化成全表扫描。
    /// </summary>
    private void TryCreateIndexes(SqliteConnection conn)
    {
        int attempts;

        lock (SchemaLock)
        {
            if (IndexRetry.TryGetValue(_dbPath, out var state))
            {
                if (state.Attempts >= MaxIndexAttempts) return;

                if (DateTime.UtcNow < state.NextTryUtc) return;

                attempts = state.Attempts;
            }
            else
            {
                attempts = 0;
            }
        }

        // 先用 temp_store=MEMORY 降低对临时文件的依赖(排序放内存,本表规模下只有几 MB)
        try
        {
            using var tmp = conn.CreateCommand();
            tmp.CommandText = "PRAGMA temp_store=MEMORY;";
            tmp.ExecuteNonQuery();
        }
        catch
        {
            // 设不上不影响建表;索引最多退化成用临时文件
        }

        // fatal:false —— 逐条尽力而为,失败的汇总成本库的错误记录,成功的照样建上
        var failure = ExecuteStatements(conn, HistorySchema.CreateIndexStatements, fatal: false);

        lock (SchemaLock)
        {
            if (failure is null)
            {
                IndexRetry.Remove(_dbPath);
                IndexErrors.Remove(_dbPath);

                return;
            }

            var next = attempts + 1;

            // 指数退避,封顶 60 秒
            var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, next)));

            IndexRetry[_dbPath] = (next, DateTime.UtcNow + delay);

            IndexErrors[_dbPath] = failure + " —— 第 " + next + " 次建索引失败"
                + (next >= MaxIndexAttempts
                    ? ",已达重试上限,本进程不再尝试(这些查询会退化成全表扫描)"
                    : ",将在 " + (int)delay.TotalSeconds + " 秒后重试");
        }
    }

    /// <summary>
    /// 顺序执行一批 DDL。
    /// <paramref name="fatal"/>=true 时失败即抛(建表);false 时**把每一条的错误都收齐**再返回(建索引)。
    ///
    /// 建索引为什么必须"收齐"而不是"见错就返回":DDL 之间没有依赖,
    /// 第一条挂掉不代表后面两条也建不成。早先 return 掉了,于是 idx_history_var_id
    /// (每变量最新值的关键索引)永远建不上。
    /// </summary>
    private string? ExecuteStatements(
        SqliteConnection conn, IReadOnlyList<string> statements, bool fatal)
    {
        var errors = new List<string>();

        foreach (var sql in statements)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                if (fatal)
                {
                    // 必须带上"是哪一条 DDL":底层只报 "unable to open database file"
                    // 时现场根本分不清是建表失败还是建索引失败,而两者处置完全不同。
                    throw new InvalidOperationException(
                        "初始化历史库失败(" + _dbPath + ")。失败语句: " + sql
                        + " —— " + ex.Message, ex);
                }

                errors.Add(sql + " —— " + ex.Message);
            }
        }

        return errors.Count == 0 ? null : string.Join(" ; ", errors);
    }

    /// <summary>
    /// 尝试把库切到 WAL,失败则按指数退避安排下次重试。
    ///
    /// journal_mode 是库级持久设置,切换要抢独占锁 ——
    /// 所以只在建库时做一次,而且**不能无限重试**(见 <see cref="WalRetry"/> 的注释)。
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

            // **必须看返回值**:库被别的连接占着、或老库停在 delete 模式时,
            // 这句会"执行成功但没切过去",返回的仍是 delete。
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

                // 只清**本库**的失败记录:多库场景下别的库还卡在 delete 模式,
                // 此处切成功不该把它的记录一并抹掉。
                WalErrors.Remove(_dbPath);

                return;
            }

            var next = attempts + 1;

            // 指数退避,封顶 60 秒
            var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, next)));

            WalRetry[_dbPath] = (next, DateTime.UtcNow + delay);

            WalErrors[_dbPath] = "未能启用 WAL(第 " + next + " 次,返回 " + (mode ?? "null") + ")"
                + (error is null ? "" : ":" + error)
                + (next >= MaxWalAttempts ? " —— 已达重试上限,本进程不再尝试" : ",将在 " + (int)delay.TotalSeconds + " 秒后重试");
        }
    }

    /// <summary>
    /// 取变量 ID,不存在则创建。带内存缓存。
    ///
    /// **已知限制(需要改表才能修,见 docs/13-历史库改造方案-跨设备与时间精度.md)**:
    /// VarDict 的唯一键只有 VarName,DeviceName 只是附带信息。若同一个 history.db 上
    /// 有两台设备出现同名变量,第二台会复用第一台的 VarId,两者历史数据混在一起
    /// (而且事后无法拆分)。当前现场配置单文件内跨设备无重名,属于潜伏问题。
    /// </summary>
    public int GetOrCreateVarId(string varName, string deviceName = "", string unit = "")
    {
        lock (_cacheLock)
        {
            if (_varIdCache.TryGetValue(varName, out var cached))
            {
                return cached;
            }
        }

        EnsureSchema();

        using var conn = OpenConnection();

        return GetOrCreateVarIdCore(conn, varName, deviceName, unit);
    }

    /// <summary>
    /// 在**已打开**的连接上"取或建"变量 ID,并写进缓存。
    /// 批量路径(WritePoints)靠它做到一批变量只开一次连接。调用方负责连接生命周期与建表。
    /// </summary>
    private int GetOrCreateVarIdCore(SqliteConnection conn, string varName, string deviceName, string unit)
    {
        lock (_cacheLock)
        {
            if (_varIdCache.TryGetValue(varName, out var cached))
            {
                return cached;
            }
        }

        using var cmd = conn.CreateCommand();

        // 一条语句搞定"取或建":
        // 先 INSERT ... ON CONFLICT DO NOTHING(VarName 上有 UNIQUE),再 SELECT 回来。
        // 老写法是"先 SELECT,查不到再 INSERT",两个线程/两个实例同时首写同一个变量时,
        // 双方都查不到 → 都去 INSERT → 后者撞 UNIQUE 抛 SqliteException,
        // 那个采集通道的归档就此断掉(而且每次重试都撞)。这里靠唯一约束做原子去重。
        cmd.CommandText =
            "INSERT INTO VarDict (VarName, DeviceName, Unit) VALUES ($n, $d, $u) " +
            "ON CONFLICT (VarName) DO NOTHING; " +
            "SELECT VarId FROM VarDict WHERE VarName = $n;";

        cmd.Parameters.AddWithValue("$n", varName);
        cmd.Parameters.AddWithValue("$d", deviceName ?? "");
        cmd.Parameters.AddWithValue("$u", unit ?? "");

        var result = cmd.ExecuteScalar();

        if (result is null || result == DBNull.Value)
        {
            throw new InvalidOperationException ("取建变量 ID 失败: " + varName);
        }

        var id = Convert.ToInt32(result, CultureInfo.InvariantCulture);

        lock (_cacheLock) _varIdCache[varName] = id;

        return id;
    }

    /// <summary>
    /// 批量写入一批采样。**单事务**。
    /// 空批直接返回,不建事务。
    /// </summary>
    public int WriteBatch(IReadOnlyList<HistoryRecord> records)
    {
        if (records.Count == 0) return 0;

        EnsureSchema();

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        var written = 0;

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO History (SampleTime, VarId, Value, Quality) " +
                "VALUES ($t, $v, $val, $q)";

            var pTime = cmd.Parameters.Add("$t", SqliteType.Text);
            var pVar = cmd.Parameters.Add("$v", SqliteType.Integer);
            var pVal = cmd.Parameters.Add("$val", SqliteType.Real);
            var pQ = cmd.Parameters.Add("$q", SqliteType.Integer);

            foreach (var r in records)
            {
                pTime.Value = DbTime.Format(r.SampleTime, HistorySchema.TimeFormat);
                pVar.Value = r.VarId;
                pVal.Value = r.Value;
                pQ.Value = r.Quality;

                written += cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();

        return written;
    }

    /// <summary>
    /// 批量写入,但**跳过已存在的同一 (变量, 采样时刻)**。
    ///
    /// 只给数据迁移用:老库搬迁难免要跑第二遍(第一次中途被杀、客户多按了一次)。
    /// 用 WHERE NOT EXISTS 让重复执行变成幂等的,而不是靠"记得清库"。
    /// 实时采集不走这里 —— 采集端本来就一次一条,不走这个多一次索引判断的成本。
    /// </summary>
    public int WriteBatchIfAbsent(IReadOnlyList<HistoryRecord> records)
    {
        if (records.Count == 0) return 0;

        EnsureSchema();

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        var written = 0;

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO History (SampleTime, VarId, Value, Quality) " +
                "SELECT $t, $v, $val, $q " +
                "WHERE NOT EXISTS (SELECT 1 FROM History WHERE VarId = $v AND SampleTime = $t)";

            var pTime = cmd.Parameters.Add("$t", SqliteType.Text);
            var pVar = cmd.Parameters.Add("$v", SqliteType.Integer);
            var pVal = cmd.Parameters.Add("$val", SqliteType.Real);
            var pQ = cmd.Parameters.Add("$q", SqliteType.Integer);

            foreach (var r in records)
            {
                pTime.Value = DbTime.Format(r.SampleTime, HistorySchema.TimeFormat);
                pVar.Value = r.VarId;
                pVal.Value = r.Value;
                pQ.Value = r.Quality;

                written += cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();

        return written;
    }

    /// <summary>查单变量在时间区间内的记录(趋势用)。</summary>
    public List<HistoryRecord> Query(string varName, DateTime from, DateTime to, int limit = 5000)
    {
        // SQLite 的 LIMIT 0 是"一条都不返回",不是"不限条数"。
        // 调用方(含 MCP 工具、界面查询框)传 0 或负数时会被当成"没数据",必须给回默认上限。
        if (limit <= 0) limit = 5000;

        EnsureSchema();

        var varId = TryGetVarId(varName);

        if (varId is null) return new List<HistoryRecord>();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText =
            "SELECT SampleTime, VarId, Value, Quality FROM History " +
            "WHERE VarId = $v AND SampleTime >= $from AND SampleTime <= $to " +
            "ORDER BY SampleTime LIMIT $limit";

        cmd.Parameters.AddWithValue("$v", varId.Value);
        cmd.Parameters.AddWithValue("$from", DbTime.Format(from, HistorySchema.TimeFormat));
        cmd.Parameters.AddWithValue("$to", DbTime.Format(to, HistorySchema.TimeFormat));
        cmd.Parameters.AddWithValue("$limit", limit);

        return ReadRecords(cmd);
    }

    /// <summary>查时间区间内所有变量(报表用)。</summary>
    public List<HistoryRecord> QueryAll(DateTime from, DateTime to, int limit = 100000)
    {
        if (limit <= 0) limit = 100000; // LIMIT 0 会静默返回空,见 Query 的说明

        EnsureSchema();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText =
            "SELECT SampleTime, VarId, Value, Quality FROM History " +
            "WHERE SampleTime >= $from AND SampleTime <= $to " +
            "ORDER BY SampleTime LIMIT $limit";

        cmd.Parameters.AddWithValue("$from", DbTime.Format(from, HistorySchema.TimeFormat));
        cmd.Parameters.AddWithValue("$to", DbTime.Format(to, HistorySchema.TimeFormat));
        cmd.Parameters.AddWithValue("$limit", limit);

        return ReadRecords(cmd);
    }

    private static List<HistoryRecord> ReadRecords(SqliteCommand cmd)
    {
        var list = new List<HistoryRecord>();

        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            var timeText = reader.GetString(0);

            if (!DateTime.TryParseExact(timeText, HistorySchema.TimeFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                continue;
            }

            list.Add(new HistoryRecord(
                time,
                reader.GetInt32(1),
                reader.GetDouble(2),
                reader.GetInt32(3)));
        }

        return list;
    }

    private int? TryGetVarId(string varName)
    {
        lock (_cacheLock)
        {
            if (_varIdCache.TryGetValue(varName, out var cached))
            {
                return cached;
            }
        }

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT VarId FROM VarDict WHERE VarName = $n";
        cmd.Parameters.AddWithValue("$n", varName);

        var result = cmd.ExecuteScalar();

        if (result is null || result == DBNull.Value) return null;

        var id = Convert.ToInt32(result, CultureInfo.InvariantCulture);

        lock (_cacheLock) _varIdCache[varName] = id;

        return id;
    }

    /// <summary>全部变量字典(界面显示变量列表用)。</summary>
    public List<VarDictEntry> GetVarDict()
    {
        EnsureSchema();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT VarId, VarName, DeviceName, Unit FROM VarDict ORDER BY VarId";

        var list = new List<VarDictEntry>();

        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            list.Add(new VarDictEntry(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3)));
        }

        return list;
    }

    /// <summary>
    /// 删除早于保留期的数据。
    /// 保留期 0 表示永久保留,直接返回 0。
    /// </summary>
    public int DeleteBefore(DateTime cutoff)
    {
        EnsureSchema();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "DELETE FROM History WHERE SampleTime < $c";
        cmd.Parameters.AddWithValue("$c", DbTime.Format(cutoff, HistorySchema.TimeFormat));

        return cmd.ExecuteNonQuery();
    }

    /// <summary>统计行数(容量监控用)。</summary>
    public long Count()
    {
        EnsureSchema();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT COUNT(*) FROM History";

        var result = cmd.ExecuteScalar();

        return result is null || result == DBNull.Value
            ? 0L
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }
}
