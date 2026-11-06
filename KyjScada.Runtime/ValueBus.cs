using KyjScada.Contracts;

namespace KyjScada.Runtime;

/// <summary>
/// 实时值总线实现。全系统唯一写入口。
///
/// 并发模型:
///  - Publish 由采集线程独占调用(内部锁)
///  - GetSnapshot 返回不可变快照,读侧无锁
///  - 读多写少,快照复制比锁竞争更划算(129 点复制成本可忽略)
/// </summary>
public sealed class ValueBus : IValueBus
{
    private readonly object _lock = new();
    private long _version;
    private DateTime _timestamp = DateTime.MinValue;
    private bool _qualityGood;
    private Dictionary<string, TagValue> _values = new(StringComparer.OrdinalIgnoreCase);

    public long Version
    {
        get { lock (_lock) return _version; }
    }

    public void Publish (IReadOnlyDictionary<string, TagValue> values)
    {
        var copy = new Dictionary<string, TagValue>(values.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in values) copy[kv.Key] = kv.Value;

        lock (_lock)
        {
            _values = copy;
            _version++;
            _timestamp = DateTime.Now;
            _qualityGood = true;
        }
    }

    public ValueSnapshot GetSnapshot ()
    {
        lock (_lock)
        {
            return new ValueSnapshot (_version, _timestamp, _qualityGood, _values);
        }
    }

    public TagValue? GetValue (string tagName)
    {
        lock (_lock)
        {
            return _values.TryGetValue (tagName, out var v) ? v : null;
        }
    }
}
