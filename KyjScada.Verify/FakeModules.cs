using KyjScada.Contracts;

namespace KyjScada.Verify;

/// <summary>正常模块:采集一个值写入总线。</summary>
public sealed class CollectorModule : IModule
{
    public string Id => "collector";

    public string DisplayName => "通讯采集";

    public Type[] Requires => Array.Empty<Type>();

    private IModuleContext? _ctx;

    public void Init (IModuleContext context)
    {
        _ctx = context;
    }

    public Task StartAsync (CancellationToken ct)
    {
        var bus = _ctx!.GetService<IValueWriter>();

        var dict = new Dictionary<string, TagValue> ()
        {
            ["LQT_Level"] = new TagValue ("LQT_Level", 5.5, TagQuality.Good, DateTime.Now),
            ["KYJ1_OutTemp"] = new TagValue ("KYJ1_OutTemp", 39.1, TagQuality.Good, DateTime.Now),
            ["LQB1_Start"] = new TagValue ("LQB1_Start", null, TagQuality.Bad, DateTime.Now)
        };

        bus!.Publish (dict);
        _ctx!.Log.Info ("已发布 3 个测点");

        return Task.CompletedTask;
    }

    public Task StopAsync (CancellationToken ct) => Task.CompletedTask;
}

/// <summary>故意在 Init 抛异常的模块,验证故障隔离。</summary>
public sealed class BrokenInitModule : IModule
{
    public string Id => "broken.init";

    public string DisplayName => "初始化会崩的模块";

    public Type[] Requires => Array.Empty<Type>();

    public void Init (IModuleContext context)
    {
        throw new InvalidOperationException ("故意在 Init 抛出");
    }

    public Task StartAsync (CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync (CancellationToken ct) => Task.CompletedTask;
}

/// <summary>依赖不存在服务的模块,验证依赖校验。</summary>
public sealed class MissingDepModule : IModule
{
    public string Id => "missing.dep";

    public string DisplayName => "依赖缺失的模块";

    public Type[] Requires => new[] { typeof(IAlarmSink) };

    public void Init (IModuleContext context) { }

    public Task StartAsync (CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync (CancellationToken ct) => Task.CompletedTask;
}

/// <summary>不存在的服务类型,用于测试依赖校验。</summary>
public interface IAlarmSink
{
}

/// <summary>只读模块:从总线读值,验证只读模块可独立运行。</summary>
public sealed class TrendModule : IModule
{
    public string Id => "trend.realtime";

    public string DisplayName => "实时趋势";

    public Type[] Requires => new[] { typeof(IValueReader) };

    private IModuleContext? _ctx;

    public void Init (IModuleContext context)
    {
        _ctx = context;
    }

    public Task StartAsync (CancellationToken ct)
    {
        var reader = _ctx!.GetService<IValueReader>();
        var snap = reader!.GetSnapshot ();

        _ctx!.Log.Info ($"快照版本 {snap.Version},质量 {snap.QualityGood},测点 {snap.Values.Count}");

        var lqt = snap["LQT_Level"];
        _ctx!.Log.Info ($"LQT_Level = {lqt?.AsDouble ()}");

        var start = snap["LQB1_Start"];
        _ctx!.Log.Info ($"LQB1_Start = {(start?.Value == null ? "NULL(不是false)" : start.AsBool ().ToString ())}");

        return Task.CompletedTask;
    }

    public Task StopAsync (CancellationToken ct) => Task.CompletedTask;
}
