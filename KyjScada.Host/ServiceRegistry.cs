using KyjScada.Contracts;

namespace KyjScada.Host;

/// <summary>
/// 服务注册表。模块通过它取服务,宿主通过它做依赖校验。
///
/// 关键:Provide 重复注册必须报错,不能静默覆盖。
/// 两个模块都注册 IValueWriter 是架构级错误,必须炸出来。
/// </summary>
public sealed class ServiceRegistry
{
    private readonly Dictionary<Type, object> _services = new();

    public void Provide<T>(T instance) where T : class
    {
        if (instance == null)
        {
            throw new ArgumentNullException(nameof(instance));
        }

        if (_services.ContainsKey(typeof(T)))
        {
            throw new InvalidOperationException(
                $"服务 {typeof(T).Name} 已注册。同一服务不允许两个实现,否则会出现多写者。");
        }

        _services[typeof(T)] = instance;
    }

    public bool Has(Type serviceType) => _services.ContainsKey(serviceType);

    public T? Get<T>() where T : class
    {
        return _services.TryGetValue(typeof(T), out var o) ? (T)o : null;
    }

    public IReadOnlyDictionary<Type, object> All => _services;
}

public sealed class ModuleContext : IModuleContext
{
    private readonly ServiceRegistry _registry;

    public ModuleContext(ServiceRegistry registry, IModuleLogger log, IModuleConfig config)
    {
        _registry = registry;
        Log = log;
        Config = config;
    }

    public IModuleLogger Log { get; }

    public IModuleConfig Config { get; }

    public T? GetService<T>() where T : class => _registry.Get<T>();
}