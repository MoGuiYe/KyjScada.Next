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

        var records = new List<HistoryRecord>(points.Count);

        foreach (var p in points)
        {
            var id = GetOrCreateVarId(p.VarName, p.DeviceName, p.Unit);
            records.Add(new HistoryRecord(p.SampleTime, id, p.Value, p.Quality));
        }

        return WriteBatch(records);
    }

    private readonly string _dbPath;
    private readonly Dictionary<string, int> _varIdCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();

    /// <summary>建表开关。跨实例共享:同一个库文件在进程内可能被多个仓储实例打开。</summary>
    private static readonly HashSet<string> SchemaReady = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>建表(幂等)。首次使用时调用。</summary>
    public void EnsureSchema()
    {
        lock (SchemaLock)
        {
            if (SchemaReady.Contains(_dbPath)) return;

            using var conn = OpenConnection();

            // journal_mode 只在建库时切一次(它是库级持久设置),
            // 不要放进 OpenConnection:每条连接都切等于每次都要抢独占锁。
            // WAL:读写并发,写不阻塞读。老库实测就是 WAL。
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=wal;";
                pragma.ExecuteScalar();
            }

            foreach (var sql in HistorySchema.AllCreateStatements)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            SchemaReady.Add(_dbPath);
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

        // 一条语句搞定"取或建":
        // 先 INSERT ... ON CONFLICT DO NOTHING(VarName 上有 UNIQUE),再 SELECT 回来。
        // 老写法是"先 SELECT,查不到再 INSERT",两个线程/两个实例同时首写同一个变量时,
        // 双方都查不到 → 都去 INSERT → 后者撞 UNIQUE 抛 SqliteException,
        // 那个采集通道的归档就此断掉(而且每次重试都撞)。这里靠唯一约束做原子去重。
        using (var cmd = conn.CreateCommand())
        {
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
                pTime.Value = r.SampleTime.ToString(HistorySchema.TimeFormat, CultureInfo.InvariantCulture);
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
                pTime.Value = r.SampleTime.ToString(HistorySchema.TimeFormat, CultureInfo.InvariantCulture);
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
        cmd.Parameters.AddWithValue("$from", from.ToString(HistorySchema.TimeFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$to", to.ToString(HistorySchema.TimeFormat, CultureInfo.InvariantCulture));
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

        cmd.Parameters.AddWithValue("$from", from.ToString(HistorySchema.TimeFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$to", to.ToString(HistorySchema.TimeFormat, CultureInfo.InvariantCulture));
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
        cmd.Parameters.AddWithValue("$c", cutoff.ToString(HistorySchema.TimeFormat, CultureInfo.InvariantCulture));

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
