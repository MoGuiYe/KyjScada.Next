namespace KyjScada.Contracts;

/// <summary>历史采样(归档服务写入时的输入)。</summary>
public readonly record struct HistoryPoint(
    DateTime SampleTime,
    string VarName,
    string DeviceName,
    string Unit,
    double Value,
    int Quality);

/// <summary>
/// 历史仓储抽象。归档服务(Runtime)通过它写历史,
/// 具体实现由 Infrastructure 提供(SQLite 长表)。
///
/// 为什么用 HistoryPoint 而不是 Domain 的 HistoryRecord:
/// Contracts 不能依赖 Domain(Domain 反过来依赖 Contracts 的枚举)。
/// 让契约只描述"要归档什么",VarId 分配和表结构交给实现层。
/// </summary>
public interface IHistoryRepository
{
    /// <summary>写入一批采样(单事务)。返回实际写入行数。</summary>
    int WritePoints(IReadOnlyList<HistoryPoint> points);

    /// <summary>删除某时刻之前的记录(保留期清理)。返回删除行数。</summary>
    int DeleteBefore(DateTime cutoff);
}
