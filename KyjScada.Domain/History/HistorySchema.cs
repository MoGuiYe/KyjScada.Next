namespace KyjScada.Domain.History;

/// <summary>
/// 历史库表结构定义。集中在一处,避免建表和查询的列名漂移。
///
/// 表结构:
///   VarDict  变量字典(VarId 主键,VarName 唯一)
///   History  历史采样(长表,一行一个变量值)
///
/// 索引:
///   idx_history_time        按时间查(报表/时段查询)
///   idx_history_var_time    按变量+时间查(单变量趋势)
/// </summary>
public static class HistorySchema
{
    public const string VarDictTable = "VarDict";
    public const string HistoryTable = "History";

    public const string ColVarId = "VarId";
    public const string ColVarName = "VarName";
    public const string ColDeviceName = "DeviceName";
    public const string ColUnit = "Unit";

    public const string ColId = "Id";
    public const string ColSampleTime = "SampleTime";
    public const string ColValue = "Value";
    public const string ColQuality = "Quality";

    public const string IndexByTime = "idx_history_time";
    public const string IndexByVarTime = "idx_history_var_time";

    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>VarDict 建表语句。</summary>
    public static string CreateVarDictSql =>
        "CREATE TABLE IF NOT EXISTS " + VarDictTable + " (" +
        ColVarId + " INTEGER PRIMARY KEY, " +
        ColVarName + " TEXT NOT NULL UNIQUE, " +
        ColDeviceName + " TEXT, " +
        ColUnit + " TEXT" +
        ");";

    /// <summary>History 建表语句。Value 是 NOT NULL —— 只存有效值。</summary>
    public static string CreateHistorySql =>
        "CREATE TABLE IF NOT EXISTS " + HistoryTable + " (" +
        ColId + " INTEGER PRIMARY KEY AUTOINCREMENT, " +
        ColSampleTime + " TEXT NOT NULL, " +
        ColVarId + " INTEGER NOT NULL, " +
        ColValue + " REAL NOT NULL, " +
        ColQuality + " INTEGER NOT NULL DEFAULT 0" +
        ");";

    public static string CreateIndexByTimeSql =>
        "CREATE INDEX IF NOT EXISTS " + IndexByTime +
        " ON " + HistoryTable + "(" + ColSampleTime + ");";

    public static string CreateIndexByVarTimeSql =>
        "CREATE INDEX IF NOT EXISTS " + IndexByVarTime +
        " ON " + HistoryTable + "(" + ColVarId + ", " + ColSampleTime + ");";

    /// <summary>全部建表语句,按执行顺序。</summary>
    public static IReadOnlyList<string> AllCreateStatements => new[]
    {
        CreateVarDictSql,
        CreateHistorySql,
        CreateIndexByTimeSql,
        CreateIndexByVarTimeSql
    };
}
