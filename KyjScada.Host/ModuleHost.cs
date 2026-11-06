using KyjScada.Contracts;

namespace KyjScada.Host;

/// <summary>
/// 模块宿主。**这是「模块独立运行」能否成立的关键组件。**
///
/// 三件事必须做对:
///  1. 依赖拓扑排序 —— 启动顺序由模块自己声明的 Requires 推导,不靠人排
///  2. 单模块故障隔离 —— 一个模块崩了,不许拖垮其他模块
///  3. 可裁剪加载 —— 只加载指定模块,不加载的不启动(独立调试的基础)
/// </summary>
public sealed class ModuleHost
{
    private readonly ServiceRegistry _services = new();
    private readonly List<LoadedModule> _loaded = new();
    private readonly HostLogger _log;
    private readonly IModuleConfigSource _configSource;

    public ModuleHost(HostLogger log, IModuleConfigSource configSource)
    {
        _log = log;
        _configSource = configSource;
    }

    /// <summary>注册服务。必须在 Load 之前完成。</summary>
    public void Provide<T>(T instance) where T : class
    {
        _services.Provide (instance);
    }

    /// <summary>
    /// 加载并初始化指定模块。
    /// 传入空集合 = 加载全部已注册模块。
    /// </summary>
    public LoadReport Load(IEnumerable<IModule> allModules, IReadOnlyCollection<string>? onlyIds = null)
    {
        var report = new LoadReport();

        var candidates = allModules.ToList();
        if (onlyIds != null && onlyIds.Count > 0)
        {
            candidates = candidates.Where(m => onlyIds.Contains(m.Id)).ToList();
        }

        // 拓扑排序:依赖必须先启动
        var ordered = TopologicalSort(candidates, report);

        foreach (var module in ordered)
        {
            // 依赖校验:缺服务则拒绝加载,而不是等到运行时崩
            var missing = module.Requires
                .Where(t => !_services.Has (t))
                .ToList();

            if (missing.Count > 0)
            {
                report.Rejected.Add(new RejectInfo(
                    module.Id,
                    module.DisplayName,
                    "缺少依赖服务: " + string.Join(", ", missing.Select(t => t.Name))));

                _log.Warn($"模块 {module.Id} 拒绝加载: 缺少 {string.Join(", ", missing.Select(t => t.Name))}");
                continue;
            }

            try
            {
                var ctx = new ModuleContext(_services, _log.ForModule(module.Id), _configSource.ForModule(module.Id));
                module.Init (ctx);

                _loaded.Add(new LoadedModule(module, ctx));
                report.Loaded.Add(module.Id);
                _log.Info($"模块 {module.Id} ({module.DisplayName}) 初始化完成");
            }
            catch (Exception ex)
            {
                // 初始化失败:记录并跳过,不影响其他模块
                report.Failed.Add(new FailInfo(module.Id, module.DisplayName, "Init", ex));
                _log.LogError ($"模块 {module.Id} 初始化失败", ex);
            }
        }

        return report;
    }

    /// <summary>按加载顺序启动全部模块。单模块失败不影响其他。</summary>
    public async Task<LoadReport> StartAllAsync(CancellationToken ct)
    {
        var report = new LoadReport();

        foreach (var lm in _loaded)
        {
            try
            {
                await lm.Module.StartAsync (ct);
                report.Loaded.Add(lm.Module.Id);
                _log.Info($"模块 {lm.Module.Id} 已启动");
            }
            catch (Exception ex)
            {
                report.Failed.Add(new FailInfo(lm.Module.Id, lm.Module.DisplayName, "Start", ex));
                _log.LogError ($"模块 {lm.Module.Id} 启动失败,已隔离", ex);
            }
        }

        return report;
    }

    /// <summary>逆序停止。单模块失败不影响其他。</summary>
    public async Task StopAllAsync(CancellationToken ct)
    {
        foreach (var lm in _loaded.AsEnumerable().Reverse())
        {
            try
            {
                await lm.Module.StopAsync (ct);
            }
            catch (Exception ex)
            {
                _log.LogError ($"模块 {lm.Module.Id} 停止失败", ex);
            }
        }
    }

    public IReadOnlyList<LoadedModule> Loaded => _loaded;

    /// <summary>
    /// 依赖拓扑排序。检测循环依赖并报告。
    /// </summary>
    private static List<IModule> TopologicalSort(List<IModule> modules, LoadReport report)
    {
        var byId = modules.ToDictionary(m => m.Id);
        var result = new List<IModule>();
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();

        void Visit(IModule m)
        {
            if (visited.Contains(m.Id)) return;

            if (!visiting.Add(m.Id))
            {
                report.Cycles.Add(m.Id);
                return;
            }

            // 依赖的服务若由某个模块提供,那个模块要先启动。
            // 这里用约定:模块 Id 与它提供的服务类型做弱关联不可靠,
            // 所以只按 Requires 中「有同名模块」的情况排序。
            foreach (var dep in m.Requires)
            {
                var provider = modules.FirstOrDefault(x => x.Id == dep.Name.ToLowerInvariant());
                if (provider != null && provider.Id != m.Id)
                {
                    Visit(provider);
                }
            }

            visiting.Remove(m.Id);
            visited.Add(m.Id);
            result.Add(m);
        }

        foreach (var m in modules) Visit(m);

        return result;
    }
}

public sealed class LoadedModule
{
    public IModule Module { get; }

    public IModuleContext Context { get; }

    public LoadedModule(IModule module, IModuleContext context)
    {
        Module = module;
        Context = context;
    }
}

public sealed class LoadReport
{
    public List<string> Loaded { get; } = new();

    public List<RejectInfo> Rejected { get; } = new();

    public List<FailInfo> Failed { get; } = new();

    public List<string> Cycles { get; } = new();

    public bool AllOk => Rejected.Count == 0 && Failed.Count == 0 && Cycles.Count == 0;
}

public sealed record RejectInfo(string Id, string Name, string Reason);

public sealed record FailInfo(string Id, string Name, string Phase, Exception Exception);
