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

        // 整轮质量:只要还有一个点读到了有效值,就不能说"所有值都不可信"。
        // 契约里 QualityGood=false 的含义是"本快照的值一律不可信"(见 IValueBus 注释),
        // 所以这里按"是否存在 Good 点"判定 —— 断线那一轮全部是 NotCollected,
        // 于是为 false;正常轮次为 true。
        //
        // 早先这里写死 true:断线时 QualityGood 仍是 true,任何依赖它做
        // "整轮质量"判断的模块都会把一屏 NotCollected 当成正常数据。
        var qualityGood = false;

        foreach (var kv in copy)
        {
            if (kv.Value is not null && kv.Value.Quality == TagQuality.Good)
            {
                qualityGood = true;

                break;
            }
        }

        lock (_lock)
        {
            _values = copy;
            _version++;

            // 本地时钟在 DST 回拨、或机器被 NTP 往回校时都会**倒退**,
            // 而下游(归档节拍、看门狗、趋势)把这个时间当"单调基准":
            // 倒退会让归档一直判"还没到点",看门狗也可能误判。
            // 这里只保证快照时间不倒退,不改变它的本地时间语义。
            var now = DateTime.Now;

            if (now > _timestamp) _timestamp = now;

            _qualityGood = qualityGood;
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
