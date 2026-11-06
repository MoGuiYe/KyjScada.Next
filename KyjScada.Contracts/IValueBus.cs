namespace KyjScada.Contracts;

/// <summary>
/// 实时值总线。**全系统唯一的数据真相源**。
///
/// 设计要点(这是模块化的命门):
///  1. 只有一个写者:通讯采集模块。其他模块一律只读。
///  2. 读写接口物理分离(IValueWriter / IValueReader),编译期就杜绝多写者。
///  3. 读取必须拿 Snapshot,保证同一时刻的一致性视图。
///
/// 为什么必须这样:
///  旧系统 CurrentValue 是 ConcurrentDictionary,虽然线程安全,
///  但两个模块在不同时刻各读一次,会拿到不同时刻的值。
///  做联锁判断时这会导致「A 说已启动、B 说未启动」的自相矛盾。
/// </summary>
public interface IValueBus : IValueReader, IValueWriter
{
}

/// <summary>只读侧。绝大多数模块只依赖这个。</summary>
public interface IValueReader
{
    /// <summary>当前快照版本号。每次采集完成自增。</summary>
    long Version { get; }

    /// <summary>
    /// 取一致性快照。同一 Version 内所有值来自同一次采集。
    /// 联锁/报警判断必须用这个,不要逐个 GetValue。
    /// </summary>
    ValueSnapshot GetSnapshot();

    /// <summary>单值读取(便利方法,非一致性场景才用)。</summary>
    TagValue? GetValue(string tagName);
}

/// <summary>
/// 可写侧。**只应由通讯采集模块实现并注入**。
/// 其他模块若需要写值,走 ICommandGate,不要直接拿这个。
/// </summary>
public interface IValueWriter
{
    /// <summary>提交一次采集结果。整批原子生效,Version 自增。</summary>
    void Publish(IReadOnlyDictionary<string, TagValue> values);
}

/// <summary>不可变快照。</summary>
public sealed class ValueSnapshot
{
    public long Version { get; }

    /// <summary>采集完成时刻。</summary>
    public DateTime Timestamp { get; }

    /// <summary>通讯质量。false 时所有值视为不可信。</summary>
    public bool QualityGood { get; }

    private readonly IReadOnlyDictionary<string, TagValue> _values;

    public ValueSnapshot(
        long version,
        DateTime timestamp,
        bool qualityGood,
        IReadOnlyDictionary<string, TagValue> values)
    {
        Version = version;
        Timestamp = timestamp;
        QualityGood = qualityGood;
        _values = values;
    }

    public IReadOnlyDictionary<string, TagValue> Values => _values;

    public TagValue? this[string tagName] =>
        _values.TryGetValue(tagName, out var v) ? v : null;
}

/// <summary>
/// 单个测点值。
///
/// 关键:Value 可为 null。绝不能把 NULL 当 0。
/// 旧系统 LQB1_Start 实测 6411 行中 6173 行是 NULL(96%),
/// 若默认成 false,会得出「泵一直在停」的错误结论。
/// </summary>
public sealed class TagValue
{
    public string Name { get; }

    /// <summary>原始值。null = 无数据/坏值。</summary>
    public object? Value { get; }

    public TagQuality Quality { get; }

    /// <summary>值变化时刻(不是采集时刻)。用于判断抖动。</summary>
    public DateTime ChangedAt { get; }

    public TagValue(string name, object? value, TagQuality quality, DateTime changedAt)
    {
        Name = name;
        Value = value;
        Quality = quality;
        ChangedAt = changedAt;
    }

    public double? AsDouble() => Value switch
    {
        null => null,
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        bool bo => bo ? 1d : 0d,
        string str => double.TryParse(str, out var p) ? p : null,
        _ => null
    };

    public bool? AsBool() => Value switch
    {
        null => null,
        bool b => b,
        double d => d != 0,
        float f => f != 0,
        int i => i != 0,
        short s => s != 0,
        string str => str is "1" or "true" or "True",
        _ => null
    };
}

public enum TagQuality
{
    /// <summary>正常。</summary>
    Good = 0,

    /// <summary>值存在但可疑(超量程等)。</summary>
    Uncertain = 1,

    /// <summary>通讯失败/无值。</summary>
    Bad = 2,

    /// <summary>该点未参与本次采集。</summary>
    NotCollected = 3
}
