namespace KyjScada.Domain.History;

/// <summary>
/// 一条历史采样记录。**长表结构:一行一个变量值**。
///
/// 为什么用长表而不是老系统的宽表(39 列):
///  1. 加变量不需要 ALTER TABLE。老系统宽表每加一个归档变量就要补一列,
///     历史数据和新数据的列数不一致,查询时极易错位。
///  2. 跨客户复用。Q6 决定要卖给不同行业,宽表意味着每个客户一套列,
///     这是不可维护的。
///  3. 稀疏数据不浪费。老库 LQB1_Start 有 6173/6411 = 96% 是 NULL,
///     宽表为不存在的值留了一整列。
///
/// 容量实测(35 个归档变量,10 秒间隔):约 6.5 GB/年,可接受。
/// </summary>
public sealed record HistoryRecord(
    DateTime SampleTime,
    int VarId,
    double Value,
    int Quality);

/// <summary>变量字典项。VarId 是历史表的外键。</summary>
public sealed record VarDictEntry(
    int VarId,
    string VarName,
    string DeviceName,
    string Unit);

/// <summary>质量码。与 TagQuality 对齐,但独立定义避免历史层依赖 Contracts。</summary>
public static class HistoryQuality
{
    public const int Good = 0;
    public const int Uncertain = 1;
    public const int Bad = 2;
}
