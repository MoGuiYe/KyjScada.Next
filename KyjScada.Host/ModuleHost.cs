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

    /// <summary>
    /// 服务 → 提供它的模块 Id。由集成方在 Provide 时显式声明。
    /// 契约里没有"模块提供哪些服务"的字段,靠 Id 和服务名猜是不可靠的
    /// (现实里的 Id 是 "trend.realtime" 这种点分写法,跟 IValueReader 对不上),
    /// 所以拓扑排序优先用这张表;没声明的才退回按名字猜。
    /// </summary>
    private readonly Dictionary<Type, string> _providerModules = new();

    /// <summary>
    /// 注册服务。必须在 Load 之前完成。
    /// </summary>
    /// <param name="providerModuleId">
    /// 可选:这个服务由哪个模块提供(模块的 Id)。给了它,依赖方就会排在那个模块后面。
    /// </param>
    public void Provide<T>(T instance, string? providerModuleId = null) where T : class
    {
        _services.Provide (instance);

        if (!string.IsNullOrWhiteSpace (providerModuleId))
        {
            _providerModules[typeof(T)] = providerModuleId!;
        }
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
    private List<IModule> TopologicalSort(List<IModule> modules, LoadReport report)
    {
        // 先按 Id 去重。原来直接 modules.ToDictionary(m => m.Id):
        // 有两个模块 Id 撞了会抛 ArgumentException,而这个方法在 Load 的
        // per-module try 外面 —— 整个宿主加载当场崩掉,报告里一条记录都没有,
        // 现场只能看到"程序起不来"。这里改成:报告重复,保留第一个。
        var byId = new Dictionary<string, IModule>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in modules)
        {
            var id = m.Id ?? "";

            if (id.Length == 0)
            {
                report.Rejected.Add(new RejectInfo("(空)", m.DisplayName, "模块 Id 为空"));

                continue;
            }

            if (byId.ContainsKey(id))
            {
                report.Rejected.Add(new RejectInfo(id, m.DisplayName,
                    "模块 Id 与已注册模块重复,已忽略后一个"));

                continue;
            }

            byId[id] = m;
        }

        modules = byId.Values.ToList();

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
            // 契约里没有"模块提供哪些服务"的显式声明,只能按命名约定反推,
            // 所以这里必须宽松且**忽略大小写**:原来只比 x.Id == dep.Name.ToLowerInvariant(),
            // 于是 Id 写成 "HistoryStore" 的模块配不上 IHistoryStore 依赖,
            // 拓扑顺序丢了一半 —— 依赖方先启动,再在启动期崩掉。
            foreach (var dep in m.Requires)
            {
                // 先看集成方有没有显式声明"这个服务由哪个模块提供",
                // 声明了就精确排序;没声明才退回按名字猜(猜不中就是没边,由依赖校验兜底)。
                IModule? provider;

                if (_providerModules.TryGetValue(dep, out var providerId))
                {
                    provider = modules.FirstOrDefault(
                        x => string.Equals(x.Id, providerId, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    provider = FindProvider(modules, dep);
                }

                if (provider != null && provider.Id != m.Id)
                {
                    _log.Info($"依赖排序: {dep.Name} 由模块 {provider.Id} 提供,排在 {m.Id} 之前");

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

    /// <summary>
    /// 按命名约定为依赖服务猜提供者:服务类型名 <c>IHistoryStore</c>
    /// 依次尝试 "ihistorystore"、"historystore"、"historystoremodule"、"historystoremodule" 等写法,
    /// 全部忽略大小写。猜不到返回 null(由调用方的"缺依赖服务"分支去报告)。
    /// </summary>
    private static IModule? FindProvider(List<IModule> modules, Type service)
    {
        var name = service.Name;
        var bare = name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]) ? name.Substring(1) : name;

        foreach (var candidate in new[] { name, bare })
        {
            foreach (var suffix in new[] { "", "Module", "Service", "Host" })
            {
                var wanted = candidate + suffix;

                var hit = modules.FirstOrDefault(
                    x => string.Equals(x.Id, wanted, StringComparison.OrdinalIgnoreCase));

                if (hit != null) return hit;
            }
        }

        return null;
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
