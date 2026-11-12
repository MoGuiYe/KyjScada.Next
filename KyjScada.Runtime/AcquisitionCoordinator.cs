using KyjScada.Contracts;
using KyjScada.Domain.Config;

namespace KyjScada.Runtime;

/// <summary>单台设备重建的结果。</summary>
public enum RestartOutcome
{
    /// <summary>已换新:返回的流水线就是台账里当前那一条。</summary>
    Replaced,

    /// <summary>
    /// 没拿到重建权(超过 <see cref="AcquisitionCoordinator.RebuildTimeout"/>)。
    ///
    /// **台账与旧流水线一个字节都没动,旧连接仍在采集** ——
    /// 调用方必须保留手里的旧引用。当成"重建失败"清掉的话,
    /// 界面与看门狗就都失去了对那条还在跑的流水线的引用:
    /// 没人显示、没人监视、没人能停,却一直占着 COM 口(幽灵采集)。
    /// </summary>
    Busy,

    /// <summary>
    /// 拿到重建权了,但期间有更新的一轮重建跑过:台账已由那一轮决定,本次结果作废。
    /// 注意这条路径上旧流水线**已经**被本次释放了,所以调用方手里的旧引用不能再当"还在跑"用。
    /// </summary>
    Superseded,

    /// <summary>设备名无效,没有可重建的对象(台账未动)。</summary>
    NotAttempted
}

/// <summary>单台设备重建的结果与流水线。只看 <see cref="Pipeline"/> 为 null 是分不清
/// "没轮到"和"没建起来"的 —— 那正是幽灵采集的来源。</summary>
public readonly record struct RestartResult(RestartOutcome Outcome, DevicePipeline? Pipeline)
{
    /// <summary>是否真的换了新流水线。</summary>
    public bool Replaced => Outcome == RestartOutcome.Replaced;
}

/// <summary>
/// 多设备并发采集调度器:为每台可采设备建一条 <see cref="DevicePipeline"/> 并一起跑。
///
/// 职责边界:
///  - 只负责"起停 + 台账",不做协议、不碰数据库(靠委托注入);
///  - 设备清单变化时 <see cref="Rebuild"/> 重排,不整体重启(避免无谓断线);
///  - 退出时统一 StopAll 并等待,防止关窗口后后台还在读串口。
/// </summary>
public sealed class AcquisitionCoordinator : IDisposable
{
    private readonly Dictionary<string, DevicePipeline> _pipelines = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>
    /// 重建串行化。
    ///
    /// 异步重建里有一段 await(释放旧流水线),两个重建重叠时会出大问题:
    /// 第二次建好 P_B 之后,第一次回来执行"清空重建设备"会把 P_B
    /// **同步 Dispose** —— 界面指向已释放的流水线、后台在采另一条,
    /// 窗口期两条 Task 还会抢同一个 COM 口。整段重建必须互斥。
    /// </summary>
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);

    /// <summary>
    /// 等待"重建权"的上限。超过就放弃本次重建(返回 null),不把界面调用方无限挂住。
    /// 取 30 秒:远大于"释放一台旧流水线"的最坏耗时(约 5 秒),又不会让用户干等。
    /// </summary>
    public TimeSpan RebuildTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 代际号:每次"全量重建"递增。
    /// await 回来之后如果代际已经变了,说明有更新的重建在跑,本次结果直接丢弃,
    /// 不能再去动 _pipelines(否则会把新一轮刚建好的流水线顶掉)。
    /// </summary>
    private int _generation;

    private readonly Func<DeviceConfig, IAcquisitionService?> _createService;
    private readonly Func<DeviceConfig, AlarmService?>? _createAlarm;
    private readonly Func<DeviceConfig, ArchiveService?>? _createArchive;

    /// <param name="createService">建采集器;返回 null 表示该设备不可采。</param>
    /// <param name="createAlarm">建报警状态机(可选)。</param>
    /// <param name="createArchive">建归档器(可选)。</param>
    public AcquisitionCoordinator(
        Func<DeviceConfig, IAcquisitionService?> createService,
        Func<DeviceConfig, AlarmService?>? createAlarm = null,
        Func<DeviceConfig, ArchiveService?>? createArchive = null)
    {
        _createService = createService ?? throw new ArgumentNullException(nameof(createService));
        _createAlarm = createAlarm;
        _createArchive = createArchive;
    }

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 释放流水线**之前**要等的动作(由界面注入:等命令闸门里在途的写帧结束)。
    ///
    /// 为什么必须有:释放的最后一步是 <c>Service.Dispose()</c>,它会关掉串口句柄。
    /// 若此刻正好有一帧写在下发途中,关句柄与那帧读回抢同一个句柄 ——
    /// 结果是写结果不明、串口状态错乱。`Writer = null` 挡不住已经进入
    /// <c>WriteVariable</c> 的那一帧,只有"等它结束"才行。
    /// </summary>
    public Func<TimeSpan, Task<bool>>? BeforeRelease { get; set; }

    /// <summary>报警事件落库回调(所有设备共用)。</summary>
    public Func<IReadOnlyList<AlarmEvent>, int>? AlarmSink { get; set; }

    public IReadOnlyList<DevicePipeline> Pipelines
    {
        get { lock (_lock) return _pipelines.Values.ToList(); }
    }

    public DevicePipeline? Find(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return null;

        lock (_lock)
        {
            return _pipelines.TryGetValue(deviceName, out var p) ? p : null;
        }
    }

    /// <summary>
    /// 为一批设备建流水线(已存在的设备保留原实例,不打断正在采的连接)。
    ///
    /// **调用方必须已经持有 <see cref="_rebuildGate"/>** —— 这里不再自带同步,
    /// 也不再负责释放被替换下来的旧流水线(它只建,不拆)。
    ///
    /// 早先这是公开的 Build(devices, …, dropMissing:true),dropMissing 分支里直接
    /// <c>d.Dispose()</c>:既绕过重建锁、又绕过 InFlightWait,还在界面线程上同步等
    /// StopAndWait(最长 2 秒)。生产代码已改用 <see cref="ReplaceAllAsync"/> /
    /// <see cref="DropAllAsync"/> / <see cref="RestartAsync"/> / <see cref="ReleaseAllAsync"/>,
    /// 那条旁路因此收进 private,只在临界区内被调用 —— 后续改动碰不到它。
    /// </summary>
    private IReadOnlyList<DevicePipeline> BuildInto(IEnumerable<DeviceConfig> devices, Func<string, bool>? canCollect = null)
    {
        var added = new List<DevicePipeline>();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.DeviceName)) continue;

            lock (_lock)
            {
                keep.Add(device.DeviceName);

                if (_pipelines.ContainsKey(device.DeviceName)) continue;
            }

            // 工厂调用(建采集器/报警/归档)**必须放在锁外**:
            // 它们会打开串口、解析配置,持锁执行会阻塞 Pipelines/Find/Describe
            // 这些界面线程要用的读侧,现场表现就是"选设备时界面卡住"。
            //
            // 代价是"查重—创建"之间不再原子:两个并发 Build 可能为同一台设备
            // 各建一个实例。用锁外的二次检查 + 全程只写一次来收拾(见下)。
            var svc = canCollect is not null && !canCollect(device.ProtocolKey) ? null : _createService(device);

            var pipeline = new DevicePipeline(
                device,
                new ValueBus(),
                svc,
                _createAlarm?.Invoke(device),
                _createArchive?.Invoke(device),
                svc is null ? "该协议 v1 不采集" : null)
            {
                Interval = Interval,
                AlarmSink = AlarmSink,
                // 释放这条流水线前,先等命令闸门里在途的那一帧写结束 ——
                // Service.Dispose() 会关串口句柄,与正在发帧的写抢同一个句柄。
                InFlightWait = BeforeRelease
            };

            var replaced = false;

            lock (_lock)
            {
                // 二次检查:期间已有别的线程建好了,就丢弃本次新建的实例(释放掉),
                // 绝不能覆盖 —— 覆盖会把别人已经 Start 的流水线变成孤儿。
                if (_pipelines.ContainsKey(device.DeviceName))
                {
                    replaced = true;
                }
                else
                {
                    _pipelines[device.DeviceName] = pipeline;
                }
            }

            if (replaced)
            {
                pipeline.Dispose();

                continue;
            }

            added.Add(pipeline);
        }

        return added;
    }

    /// <summary>
    /// 重建单台设备,且**不在调用线程上等待旧流水线释放**。
    ///
    /// 旧流水线一律走 <see cref="DevicePipeline.ReleaseAsync"/>(先等闸门里在途的写帧收尾,
    /// 再停采集循环、关串口句柄),不在界面线程上做同步 StopAndWait ——
    /// 早先那个同步的 Restart() 已删除:它绕过 _rebuildGate、绕过 InFlightWait,
    /// 还整窗冻结 2 秒,生产代码早已没有调用方,留着只会被后来的改动误用。
    /// 顺序仍然保证(先放旧连接再建新的),但界面线程最多只被 await 挂起,
    /// 不会像同步 StopAndWait 那样整窗冻结。
    ///
    /// **返回值必须区分"没轮到"和"失败了"**:早先只返回 <c>DevicePipeline?</c>,
    /// 调用方拿 null 只能猜,猜错就是"把仍在跑的旧流水线当失败清掉"(幽灵采集)。
    /// </summary>
    /// <returns>见 <see cref="RestartResult"/>。</returns>
    public async Task<RestartResult> RestartAsync(DeviceConfig device)
    {
        // 名字无效:没有可重建的对象。台账保持原样,调用方也别动自己的引用。
        if (string.IsNullOrWhiteSpace(device.DeviceName))
        {
            return new RestartResult(RestartOutcome.NotAttempted, null);
        }

        // 必须互斥,但**不能无限等**:上一次重建若卡在释放(InFlightWait 超时 +
        // 关串口异常),后来者的重建请求会一直排队 —— 界面表现为"点重新连接没反应"。
        // 等不到就放弃本次:**台账与旧流水线一个字节都没动**,旧连接仍在采集。
        if (!await _rebuildGate.WaitAsync(RebuildTimeout).ConfigureAwait(true))
        {
            return new RestartResult(RestartOutcome.Busy, null);
        }

        try
        {
            int generation;

            DevicePipeline? old;

            lock (_lock)
            {
                generation = _generation;

                _pipelines.Remove(device.DeviceName, out old);
            }

            if (old is not null) await old.ReleaseAsync().ConfigureAwait(true);

            // await 期间若有更新的重建跑过,本次结果作废:
            // 再往下走会把新一轮刚建好的流水线当成"多余的"清掉。
            // 注意这时台账**已经不由本次调用决定了**(旧的那条已被本次释放),
            // 所以不能报 Busy —— 那会让调用方以为手里的旧引用还能用。
            lock (_lock)
            {
                if (_generation != generation)
                {
                    return new RestartResult(RestartOutcome.Superseded, null);
                }
            }

            BuildInto(new[] { device });

            var fresh = Find(device.DeviceName);

            fresh?.Start();

            return new RestartResult(RestartOutcome.Replaced, fresh);
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    /// <summary>
    /// 停掉并释放**全部**流水线,释放过程不在调用线程上等。
    ///
    /// 供"切设备/重选设备"用:早先是 Build(一批评) + 同步 Dispose,
    /// 换一台设备要等旧连接最长 2 秒,现场就是"点一下卡两秒"。
    ///
    /// **注意**:本方法在返回时就已经放掉了 _rebuildGate,所以它后面**不能**再跟一句
    /// 需要原子性的 Build —— "切到某台设备"请改用 <see cref="ReplaceAllAsync"/>。
    /// </summary>
    public async Task DropAllAsync()
    {
        // 这里**故意不限时**:本路径是"切设备/关窗",必须真的清空才算做完,
        // 放弃会让界面以为旧设备已停而实际还在采(幽灵采集)。
        //
        // 不会有永久死锁:try/finally 保证任何异常路径都会 Release,所以持锁时间
        // 有上界 = 每台流水线最长 InFlightWait(3s) + StopAndWait(2s)。
        // 真正需要"给不出结果就返回"的是 RestartAsync,那里已按 RebuildTimeout 收口。
        await _rebuildGate.WaitAsync().ConfigureAwait(true);

        try
        {
            List<DevicePipeline> all;

            lock (_lock)
            {
                // 代际 +1:此刻起,任何"还在 await 里"的旧重建都算过期
                _generation++;

                all = _pipelines.Values.ToList();
                _pipelines.Clear();
            }

            foreach (var p in all) await p.ReleaseAsync().ConfigureAwait(true);
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    /// <summary>
    /// 用给定设备**整体替换**全部流水线:释放旧的与建新的都在**同一个临界区内**完成
    /// (全程持 <see cref="_rebuildGate"/>)。
    ///
    /// 为什么必须是一个原子操作,而不能像早先那样拆成
    /// <c>await DropAllAsync(); Build(...dropMissing:true...);</c>
    /// (那个公开的 Build 已于本轮收进 private 的 <see cref="BuildInto"/>,见其注释):
    ///   · <see cref="DropAllAsync"/> 在 finally 里就把 _rebuildGate 放掉了,紧随其后的
    ///     Build **不在临界区**里。快速切 A→B 时,后到的 A 的
    ///     清理分支会把 B 刚建好的流水线**同步 Dispose** 掉
    ///     (走的是 Dispose 而不是 ReleaseAsync,还绕过 InFlightWait,不等在途写帧);
    ///   · 而 B 那边的异步续体随后照样把**已经释放**的对象写进界面的 _pipeline 与
    ///     _gate.Writer —— 现场表现就是"选中的设备不采集、写入打到已关闭的连接"。
    ///
    /// 现在"清空—重建"之间不再有别人能看见的空窗:拿锁的那一次调用一定是最后生效的那次,
    /// 台账里只会留下它建的那一台,先前调用的结果要么被本次释放、要么根本还没建。
    /// </summary>
    /// <returns>替换后该设备的流水线;<paramref name="device"/> 无效时返回 null。</returns>
    public async Task<DevicePipeline?> ReplaceAllAsync(DeviceConfig device)
    {
        if (device is null || string.IsNullOrWhiteSpace(device.DeviceName)) return null;

        // 与 DropAllAsync 一样故意不限时:本路径是"切设备",必须真的清空才算做完,
        // 放弃会让界面以为旧设备已停而实际还在采(幽灵采集)。
        // 持锁时间有上界 = 每台流水线最长 InFlightWait(3s) + StopAndWait(2s),
        // 不会永久死锁(try/finally 保证任何异常路径都会 Release)。
        await _rebuildGate.WaitAsync().ConfigureAwait(true);

        try
        {
            List<DevicePipeline> all;

            lock (_lock)
            {
                // 代际 +1:此刻起,任何"还在 await 里"的旧重建都算过期
                _generation++;

                all = _pipelines.Values.ToList();
                _pipelines.Clear();
            }

            foreach (var p in all) await p.ReleaseAsync().ConfigureAwait(true);

            // 仍然持着 _rebuildGate:这期间没有任何重建能插进来,
            // 所以"清空"与"重建"之间不存在别人能看见的空窗。
            BuildInto(new[] { device });

            var fresh = Find(device.DeviceName);

            fresh?.Start();

            return fresh;
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    /// <summary>启动全部流水线(不可采的设备跳过,但保留台账以便界面显示原因)。</summary>
    public int StartAll()
    {
        var started = 0;

        foreach (var p in Pipelines)
        {
            if (p.Start()) started++;
        }

        return started;
    }

    public void StopAll()
    {
        foreach (var p in Pipelines) p.Stop();
    }

    /// <summary>
    /// 按给定的设备清单**整批建立**流水线:清单里没有的已有设备视为"配置已删除",
    /// 会被停掉并释放(避免幽灵采集);已有设备保留原实例,不打断正在采的连接。
    ///
    /// 与 <see cref="BuildInto"/> 的分工:本方法负责"拆"(且拆得对),BuildInto 只负责"建"。
    /// 拆除一律走 <see cref="DevicePipeline.ReleaseAsync"/> —— 先等闸门里在途的写帧结束,
    /// 再停采集循环、关串口句柄;等待放在线程池上,界面线程只被 await 挂起。
    ///
    /// 早先这段"建 + 拆"是同步公开的 Build(devices, …, dropMissing:true):拆除分支直接
    /// <c>d.Dispose()</c>,既绕过重建锁(与并发的 ReplaceAllAsync 互相拆台)、又绕过
    /// InFlightWait(在途写帧与关句柄抢串口),还在界面线程上同步等 StopAndWait 最长 2 秒。
    /// 生产代码早已全部改走 DropAllAsync / ReplaceAllAsync / RestartAsync / ReleaseAllAsync,
    /// 这里把它换成一个同样异步、同样守规矩的入口,免得后续改动再去摸那条旁路。
    /// </summary>
    public async Task<IReadOnlyList<DevicePipeline>> BuildAsync(IEnumerable<DeviceConfig> devices)
    {
        var list = devices?.Where(d => !string.IsNullOrWhiteSpace(d.DeviceName)).ToList()
                   ?? new List<DeviceConfig>();

        var keep = new HashSet<string>(list.Select(d => d.DeviceName), StringComparer.OrdinalIgnoreCase);

        // 与 ReplaceAllAsync / DropAllAsync 一样故意不限时(调用方若在界面上等,
        // 也只是 await,不会冻窗口);持锁时长有上界 = 每台 InFlightWait(3s) + StopAndWait(2s)。
        await _rebuildGate.WaitAsync().ConfigureAwait(true);

        try
        {
            List<DevicePipeline> drop;

            lock (_lock)
            {
                drop = _pipelines.Where(kv => !keep.Contains(kv.Key)).Select(kv => kv.Value).ToList();

                foreach (var d in drop) _pipelines.Remove(d.DeviceName);
            }

            foreach (var d in drop) await d.ReleaseAsync().ConfigureAwait(true);

            return BuildInto(list);
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    /// <summary>各设备一行状态,给"设备总览"用。</summary>
    public string Describe()
    {
        var lines = new List<string>();

        foreach (var p in Pipelines.OrderBy(p => p.DeviceName, StringComparer.Ordinal))
        {
            lines.Add(p.DeviceName + ": " + p.State
                + "  轮次=" + p.Rounds
                + "  上轮点数=" + p.LastCount
                + (p.LastOk.HasValue ? "  最近成功=" + p.LastOk.Value.ToString("HH:mm:ss") : "")
                + (string.IsNullOrEmpty(p.LastError) ? "" : "  错误=" + p.LastError));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 同步释放全部流水线(供 IDisposable 的 finally / 测试收尾)。
    ///
    /// 在途写等待已收口进 <see cref="DevicePipeline.Dispose"/>(每台最多 3 秒),
    /// 所以这里**不再**绕过 BeforeRelease。
    ///
    /// 但它是同步的:**在界面线程上不要调它** —— 关窗请走 <see cref="ReleaseAllAsync"/>。
    /// 每台流水线要等"在途写(≤3 秒)+ 采集循环退出(≤2 秒)",同步调会把窗口挂在那儿。
    /// </summary>
    public void Dispose()
    {
        List<DevicePipeline> all;

        lock (_lock)
        {
            all = _pipelines.Values.ToList();
            _pipelines.Clear();
        }

        foreach (var p in all) p.Dispose();
    }

    /// <summary>
    /// 异步释放全部流水线(关窗路径)。
    ///
    /// 与 <see cref="DropAllAsync"/> 的区别:
    ///   · DropAllAsync 参与"重建串行化"(持 <see cref="_rebuildGate"/>、代际 +1),
    ///     而关窗时不该再去排队等一次重建 —— 窗口已经在关了;
    ///   · 两者都走 <see cref="DevicePipeline.ReleaseAsync"/>:先等闸门里在途的写帧收尾,
    ///     再停采集循环、关串口句柄。
    ///
    /// 台账同步清空(此后 <see cref="Pipelines"/> / <see cref="Find"/> 立刻为空,
    /// 界面不会再把写入挂到这些流水线上),释放本身在返回的 Task 里进行,
    /// 每台最多 3 秒在途等待 + 2 秒停止等待,但**不占用界面线程**。
    /// </summary>
    public async Task ReleaseAllAsync()
    {
        List<DevicePipeline> all;

        lock (_lock)
        {
            all = _pipelines.Values.ToList();
            _pipelines.Clear();
        }

        foreach (var p in all)
        {
            try
            {
                await p.ReleaseAsync().ConfigureAwait(false);
            }
            catch
            {
                // 关窗路径:一台释放失败不能拖住其余设备
            }
        }
    }
}
