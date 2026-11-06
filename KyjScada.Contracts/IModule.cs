namespace KyjScada.Contracts;

/// <summary>
/// 模块契约。每个模块必须实现这个,才能被宿主加载。
///
/// 这是「独立可运行」的地基:
///  宿主不关心模块内部,只按契约调用 Init/Start/Stop。
///  模块不关心谁在跑它,只依赖自己声明的服务。
/// </summary>
public interface IModule
{
    /// <summary>模块唯一标识,如 "alarm" / "trend.realtime"。</summary>
    string Id { get; }

    /// <summary>中文显示名,用于界面与日志。</summary>
    string DisplayName { get; }

    /// <summary>本模块依赖哪些服务。宿主在 Init 前校验,缺失则拒绝加载。</summary>
    Type[] Requires { get; }

    /// <summary>初始化。依赖已注入,不要在这里做耗时 IO。</summary>
    void Init(IModuleContext context);

    /// <summary>启动。这里才做真正的活(开线程、连设备、订阅)。</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>停止。必须幂等,可重复调用。</summary>
    Task StopAsync(CancellationToken ct);
}

/// <summary>
/// 模块运行上下文。模块只能通过它接触外部世界,不许自己 new 服务。
/// </summary>
public interface IModuleContext
{
    /// <summary>取服务。缺失返回 null。</summary>
    T? GetService<T>() where T : class;

    /// <summary>模块日志。自动带模块 Id 前缀。</summary>
    IModuleLogger Log { get; }

    /// <summary>模块自己的配置分区。模块之间不共享配置对象。</summary>
    IModuleConfig Config { get; }
}

public interface IModuleConfig
{
    /// <summary>取字符串配置。缺失返回 defaultValue。</summary>
    string GetString(string key, string defaultValue = "");

    int GetInt(string key, int defaultValue = 0);

    bool GetBool(string key, bool defaultValue = false);

    /// <summary>是否有该键。用于区分「未配置」与「配置为空」。</summary>
    bool Has(string key);
}

/// <summary>模块元数据,用于宿主列出可加载模块。</summary>
public sealed class ModuleDescriptor
{
    public string Id { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public string Category { get; init; } = "";

    public Type[] Requires { get; init; } = Array.Empty<Type>();

    /// <summary>是否平台级模块(与具体项目无关)。</summary>
    public bool IsPlatform { get; init; }
}
