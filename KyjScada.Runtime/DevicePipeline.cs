using KyjScada.Contracts;
using KyjScada.Domain.Config;

namespace KyjScada.Runtime;

/// <summary>
/// 一台设备的完整采集流水线:自己的连接、自己的值总线、自己的报警状态机、自己的归档节拍。
///
/// 为什么必须"每设备一条流水线":
///  老系统一次只采一台设备,切设备就是把旧连接关掉再开新的 ——
///  结果就是"看 A 的时候 B 完全没数据",历史曲线断成锯齿,报警也不会响。
///  现场有多台设备时,这段空白就是安全盲区。
///
/// 线程模型:
///  - 每台设备一个长跑 Task,自己按 <see cref="Interval"/> 循环;
///  - 设备之间**互不阻塞**:一台掉线不影响其它设备照常采集;
///  - 值为 null(未采到)时报警状态机跳过,断线不等于消除。
/// </summary>
public sealed class DevicePipeline : IDisposable
{
    /// <summary>设备配置(协议、组、变量都在里面)。</summary>
    public DeviceConfig Device { get; }

    /// <summary>本设备独占的值总线(不与其它设备混,避免同名变量互相覆盖)。</summary>
    public IValueBus Bus { get; }

    /// <summary>采集器;设备不可采时为 null(流水线退化为"仅展示配置")。</summary>
    public IAcquisitionService? Service { get; }

    /// <summary>报警状态机;null 表示本设备不做报警。</summary>
    public AlarmService? Alarm { get; }

    /// <summary>归档器;null 表示本设备不归档。</summary>
    public ArchiveService? Archive { get; }

    /// <summary>报警事件落库回调(由宿主注入,返回写入条数)。</summary>
    public Func<IReadOnlyList<AlarmEvent>, int>? AlarmSink { get; set; }

    /// <summary>采集周期。</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>不可采的原因(协议未实现/配置错误等),用于界面明确告知。</summary>
    public string? UncollectableReason { get; }

    public string DeviceName => Device.DeviceName;

    /// <summary>运行状态:未启动/正常/已连接(无数据)/未连接/异常/已停止/不可采。</summary>
    public string State { get; private set; } = "未启动";

    public int Rounds { get; private set; }

    /// <summary>最近一轮采集(含连接/超时等待)耗时毫秒,用于诊断"通讯断开时是否拖住线程"。</summary>
    public long LastCycleMs { get; private set; }

    /// <summary>最近一轮里连接阶段耗时毫秒。</summary>
    public long LastConnectMs { get; private set; }

    public int LastCount { get; private set; }

    public DateTime? LastOk { get; private set; }

    public string? LastError { get; private set; }

    public bool IsRunning => _task is { IsCompleted: false };

    /// <summary>最近一次成功的快照(界面直接读这个,不必再打总线)。</summary>
    public ValueSnapshot? LastSnapshot { get; private set; }

    private CancellationTokenSource? _cts;
    private Task? _task;
    private readonly object _lifecycle = new();

    public DevicePipeline(
        DeviceConfig device,
        IValueBus bus,
        IAcquisitionService? service,
        AlarmService? alarm = null,
        ArchiveService? archive = null,
        string? uncollectableReason = null)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        Bus = bus ?? throw new ArgumentNullException(nameof(bus));
        Service = service;
        Alarm = alarm;
        Archive = archive;
        UncollectableReason = uncollectableReason;

        if (service is null)
        {
            State = "不可采";
            Interval = TimeSpan.FromSeconds(1);
        }
    }

    /// <summary>启动本设备的采集循环(幂等:重复调用不会开两条)。</summary>
    public bool Start()
    {
        lock (_lifecycle)
        {
            if (Service is null) return false;
            if (IsRunning) return false;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _task = Task.Run(() => LoopAsync(token), CancellationToken.None);
            State = "启动中";

            return true;
        }
    }

    /// <summary>请求停止(不阻塞等待,避免关闭窗口时卡住)。</summary>
    public void Stop()
    {
        lock (_lifecycle)
        {
            _cts?.Cancel();
        }
    }

    /// <summary>停止并等待循环退出;超时返回 false(调用方据此提示,不要无声挂死)。</summary>
    public bool StopAndWait(TimeSpan timeout)
    {
        Stop();

        var task = _task;

        if (task is null) return true;

        try
        {
            return task.Wait(timeout);
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        var svc = Service!;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var sampleTime = DateTime.Now;
                var cycleWatch = System.Diagnostics.Stopwatch.StartNew();
                var got = svc.CollectOnce(Bus, sampleTime);
                LastCycleMs = cycleWatch.ElapsedMilliseconds;

                Rounds++;
                LastCount = got;

                if (svc.IsConnected)
                {
                    LastOk = sampleTime;
                    LastError = svc.LastError;
                    State = got > 0 ? "正常" : "已连接(无数据)";
                }
                else
                {
                    LastError = svc.LastError ?? "未连接";
                    State = "未连接";
                }

                // 报警与归档都用同一个快照,避免两次取快照拿到不同版本
                var snap = Bus.GetSnapshot();
                LastSnapshot = snap;

                if (got > 0 && Alarm is not null)
                {
                    var events = Alarm.ProcessSnapshot(snap);

                    if (events.Count > 0)
                    {
                        AlarmSink?.Invoke(events);
                    }
                }

                if (got > 0 && Archive is not null && Archive.ShouldWrite(sampleTime))
                {
                    Archive.Archive(snap, Device);
                }
            }
            catch (Exception ex)
            {
                // 一台设备炸了不能拖死整条流水线,更不能炸掉其它设备
                LastError = ex.Message;
                State = "异常";
            }

            try
            {
                await Task.Delay(Interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        State = "已停止";
    }

    public void Dispose()
    {
        StopAndWait(TimeSpan.FromSeconds(2));

        _cts?.Dispose();
        _cts = null;

        Service?.Dispose();
        _task = null;
    }
}