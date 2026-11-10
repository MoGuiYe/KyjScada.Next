using KyjScada.Contracts;
using KyjScada.Domain.Config;

namespace KyjScada.Runtime;

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
    /// 第二次建好 P_B 之后,第一次回来执行 Build(dropMissing:true) 会把 P_B
    /// **同步 Dispose** —— 界面指向已释放的流水线、后台在采另一条,
    /// 窗口期两条 Task 还会抢同一个 COM 口。整段重建必须互斥。
    /// </summary>
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);

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

    /// <summary>为一批设备建流水线(已存在的设备保留原实例,不打断正在采的连接)。</summary>
    /// <param name="dropMissing">true=把不在 devices 里的已有设备视为"配置已删除"并停掉;false=只增量,供 Restart 单台重建使用。</param>
    public IReadOnlyList<DevicePipeline> Build(IEnumerable<DeviceConfig> devices, Func<string, bool>? canCollect = null, bool dropMissing = true)
    {
        var added = new List<DevicePipeline>();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.DeviceName)) continue;

            keep.Add(device.DeviceName);

            DevicePipeline pipeline;

            lock (_lock)
            {
                if (_pipelines.TryGetValue(device.DeviceName, out var existing)) continue;

                var svc = canCollect is not null && !canCollect(device.ProtocolKey) ? null : _createService(device);

                pipeline = new DevicePipeline(
                    device,
                    new ValueBus(),
                    svc,
                    _createAlarm?.Invoke(device),
                    _createArchive?.Invoke(device),
                    svc is null ? "该协议 v1 不采集" : null)
                {
                    Interval = Interval,
                    AlarmSink = AlarmSink
                };

                _pipelines[device.DeviceName] = pipeline;
            }

            added.Add(pipeline);
        }

        // 配置里已经没有的设备:停掉并释放,避免幽灵采集。
        // dropMissing=false 时(只重建单台,见 Restart)不能走这里,否则会把其它设备当"已删除"停掉。
        if (dropMissing)
        {
            List<DevicePipeline> drop;

            lock (_lock)
            {
                drop = _pipelines.Where(kv => !keep.Contains(kv.Key)).Select(kv => kv.Value).ToList();

                foreach (var d in drop) _pipelines.Remove(d.DeviceName);
            }

            foreach (var d in drop) d.Dispose();
        }

        return added;
    }

    /// <summary>
    /// 重建单台设备的流水线(如改了字节序这类连接参数)。
    /// 只影响这一台,其它设备继续采。
    /// </summary>
    public DevicePipeline? Restart(DeviceConfig device)
    {
        if (string.IsNullOrWhiteSpace(device.DeviceName)) return null;

        DevicePipeline? old;

        lock (_lock)
        {
            _pipelines.Remove(device.DeviceName, out old);
        }

        old?.Dispose();

        Build(new[] { device }, null, dropMissing: false);

        var fresh = Find(device.DeviceName);

        fresh?.Start();

        return fresh;
    }

    /// <summary>
    /// 重建单台设备,且**不在调用线程上等待旧流水线释放**。
    ///
    /// 与 <see cref="Restart"/> 的差别只有一处:释放放到线程池并 await。
    /// 顺序仍然保证(先放旧连接再建新的),但界面线程最多只被 await 挂起,
    /// 不会像同步 StopAndWait 那样整窗冻结。
    /// </summary>
    public async Task<DevicePipeline?> RestartAsync(DeviceConfig device)
    {
        if (string.IsNullOrWhiteSpace(device.DeviceName)) return null;

        await _rebuildGate.WaitAsync().ConfigureAwait(true);

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
            lock (_lock)
            {
                if (_generation != generation) return null;
            }

            Build(new[] { device }, null, dropMissing: false);

            var fresh = Find(device.DeviceName);

            fresh?.Start();

            return fresh;
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
    /// </summary>
    public async Task DropAllAsync()
    {
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
}