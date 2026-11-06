using KyjScada.Contracts;
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
    private bool _schemaReady;

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
            Cache = SqliteCacheMode.Shared
        };

        var conn = new SqliteConnection(builder.ToString());
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=wal; PRAGMA synchronous=normal;";
        cmd.ExecuteNonQuery();

        return conn;
    }

    /// <summary>建表(幂等)。</summary>
    public void EnsureSchema()
    {
        if (_schemaReady) return;

        using var conn = OpenConnection();

        foreach (var sql in new[] { CreateSql })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        // 旧版本建的库没有 AckAt 列。CREATE TABLE IF NOT EXISTS 不会补列,
        // 必须显式 ALTER;列已存在时 ALTER 会报错,忽略即可。
        try
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE AlarmEvents ADD COLUMN AckAt TEXT NULL;";
            alter.ExecuteNonQuery();
        }
        catch
        {
            // 列已存在 —— 正常路径
        }

        _schemaReady = true;
    }

    /// <summary>写入一批报警事件(单事务)。空批直接返回 0。</summary>
    public int WriteEvents(IEnumerable<AlarmEvent> events)
    {
        var list = events as IReadOnlyCollection<AlarmEvent> ?? events.ToList();
        if (list.Count == 0) return 0;

        EnsureSchema();

        var now = DateTime.Now.ToString("o");

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
                pOcc.Value = ev.At.ToString("o");
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
            var p = cmd.CreateParameter(); p.ParameterName = "@f"; p.Value = from.Value.ToString("o");
            cmd.Parameters.Add(p);
        }
        if (to.HasValue)
        {
            var p = cmd.CreateParameter(); p.ParameterName = "@t"; p.Value = to.Value.ToString("o");
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

        cmd.Parameters.AddWithValue("@t", at.ToString("o"));
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

        cmd.Parameters.AddWithValue("@t", at.ToString("o"));
        cmd.Parameters.AddWithValue("@until", upto.ToString("o"));

        if (since.HasValue) cmd.Parameters.AddWithValue("@since", since.Value.ToString("o"));

        return cmd.ExecuteNonQuery();
    }

    /// <summary>删除某时刻之前的事件(保留期清理用)。</summary>
    public int DeleteBefore(DateTime time)
    {
        EnsureSchema();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM AlarmEvents WHERE OccurredAt < @t;";
        var p = cmd.CreateParameter(); p.ParameterName = "@t"; p.Value = time.ToString("o");
        cmd.Parameters.Add(p);
        return cmd.ExecuteNonQuery();
    }
}
