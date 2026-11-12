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

    /// <summary>
    /// 已释放。
    ///
    /// Dispose 超时后循环可能**还卡在一次同步读里**,等它恢复时:
    ///   · 会继续往旧 Bus 里 Publish,写出一份"设备已释放之后"的假快照;
    ///   · 还会继续跑报警/归档,把假值写进库。
    /// 所以循环在发布前必须查这个标志,看到就立刻退出。
    /// </summary>
    private volatile bool _released;

    /// <summary>
    /// 释放前等待在途写入结束的钩子(由持有 CommandGate 的一侧注入)。
    ///
    /// 为什么需要:Writer 置 null 只能挡住"之后"的写入,已经进入
    /// WriteVariable 的那一帧仍会继续用串口句柄 —— 此时 Dispose 掉 Service,
    /// 两者就抢同一个句柄(现场表现是偶发"写入失败"或串口异常)。
    /// 这个钩子让释放方先等那一帧收尾,再关连接。
    /// </summary>
    public Func<TimeSpan, Task<bool>>? InFlightWait { get; set; }

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
        catch (AggregateException) when (task.IsCanceled)
        {
            // 取消是预期路径:循环以 OperationCanceledException 结束,算正常停下
            return true;
        }
        catch (AggregateException ex)
        {
            // 循环真的挂了。对调用方来说"确实已经停了",但必须留痕 ——
            // 原来这里一律 return true 且不记状态,界面会显示"已停止",
            // 现场会以为是自己点了停止,实际是线程异常退出。
            State = "线程异常退出";
            LastError = ex.GetBaseException().Message;

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

                // 已释放:不能再发布 —— 否则会写出一份"设备已释放之后"的假快照,
                // 并连带触发报警/归档,把假值落进库
                if (_released)
                {
                    break;
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

                // 节拍判断必须与归档写库用**同一个时间戳**。
                // Archive.Archive 内部取的是 snapshot.Timestamp,而 sampleTime 是本轮采集
                // **开始前**取的 DateTime.Now —— 两者可能跨秒,于是出现"节拍说该写,
                // 落库时间戳却比上次还早"的补写/漏写。统一用快照时间。
                if (got > 0 && Archive is not null && Archive.ShouldWrite(snap.Timestamp))
                {
                    Archive.Archive(snap, Device);
                }
            }
            catch (Exception ex)
            {
                // 一台设备炸了不能拖死整条流水线,更不能炸掉其它设备。
                // 但**致命异常不能吞**:内存/栈/元数据坏掉之后,后面每一轮采集都是在
                // 损坏的进程状态上继续,谁也不知道读回来的值还算不算数。
                // 这类异常往外抛,由 StopAndWait 那两处 catch(AggregateException)
                // 把"线程异常退出"记进状态 —— 那两个分支就是为它留的。
                LastError = ex.Message;

                if (IsFatalFailure (ex))
                {
                    State = "线程异常退出";

                    throw;
                }

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

    /// <summary>判断"这一轮异常是否意味着进程状态已经不可信"。</summary>
    private static bool IsFatalFailure(Exception ex)
    {
        return ex is OutOfMemoryException
            or AccessViolationException
            or InsufficientExecutionStackException
            or BadImageFormatException;
    }

    /// <summary>
    /// 等在途写帧的上限(<see cref="ReleaseAsync"/> 与同步 <see cref="Dispose"/> 共用)。
    ///
    /// 不能无限等:写入卡死时 WaitForWritesAsync 永远等不到"写完了",
    /// 而等不到也不能把自己永久堵在这里。超时就照旧释放 —— 宁可丢一次写,
    /// 也不能让释放线程(关窗路径、切设备路径)卡死。
    /// </summary>
    public static readonly TimeSpan InFlightWaitTimeout = TimeSpan.FromSeconds(3);

    /// <summary>是否已经释放过(释放有界,最多被调一次)。</summary>
    private int _disposed;

    public void Dispose()
    {
        // 幂等:窗口关闭路径(Binding Closed)可能走两遍,释放一遍就够,
        // 第二遍再等一遍在途写纯属白等。
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // 先立标志再等:等在途写的这最多 3 秒里,采集循环不该再发布新快照
        // (否则等完写又采到一帧,现场看到的就是"停止之后还在跳数")。
        // 原先只在等完关连接后才置位,关窗路径正是踩在这段空窗上。
        _released = true;

        // **再等闸门里的在途写帧收尾**(上界 InFlightWaitTimeout)。
        //
        // 早先只有 ReleaseAsync 走这一步,Dispose 是一条直接关连接的旁路:
        // 关窗时原先调的是 _coordinator.Dispose() → 这里直接 Service.Dispose(),
        // 而 MCP 写工具可能还有一帧在下发途中 —— 与它抢同一个串口句柄,
        // 结果是写结果不明、串口异常。现在两条路径都收口在这里,
        // 调用方走 ReleaseAsync 还是 Dispose 都不会绕过在途等待。
        var wait = InFlightWait;

        if (wait is not null)
        {
            try
            {
                // 有界同步等;超时/异常都照旧往下释放
                // (关句柄正是打断卡死那一帧的手段)。
                wait(InFlightWaitTimeout).Wait(InFlightWaitTimeout);
            }
            catch
            {
                // 等待本身失败不影响释放
            }
        }

        // 超时也要继续往下走:关闭 Service 的句柄正是打断"卡在半途的同步读"的手段。
        // 但要把情况记下来,别让现场以为是自己点了停止。
        if (!StopAndWait(TimeSpan.FromSeconds(2)))
        {
            LastError ??= "停止超时:通讯调用未在 2 秒内返回";
        }

        _cts?.Dispose();
        _cts = null;

        Service?.Dispose();

        // **保留 _task**:置 null 会把"循环异常退出"这件事彻底埋掉 ——
        // 异常没人观察、IsRunning 变成假阴性(任务还在跑却报"没在跑")。
        // 这里只在任务确实已结束时才清掉引用,并观察它的异常。
        var task = _task;

        if (task is not null && task.IsCompleted)
        {
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // 取消是预期路径
            }
            catch (Exception ex)
            {
                LastError = "采集循环异常退出: " + ex.GetBaseException().Message;
            }

            _task = null;
        }
    }

    /// <summary>
    /// 释放,但**不在调用线程上等**。
    ///
    /// <see cref="Dispose"/> 里的 StopAndWait 最多阻塞 2 秒,而切设备/重读组态/
    /// 卡死看门狗这三条路径全都跑在界面线程上 —— 现场感受就是"每换一次设备卡两秒",
    /// 掉线时看门狗连续重建还会连续卡。
    ///
    /// 这里把等待放到线程池,并 await 它:**顺序仍然保证**
    /// (先停旧循环、释放串口,再让调用方去开新连接),
    /// 只是不再占用界面线程。顺序不能改成"后台随手释放"——
    /// 那样新流水线会和还没放手的旧连接抢同一个 COM 口。
    ///
    /// 在途等待不在这里单独做,已经收口进 <see cref="Dispose"/>:
    /// 早先只有本方法等 InFlightWait,直接 Dispose 的路径(关窗)绕过了它,
    /// "写完了才关连接"取决于调用方记得挑哪个方法 —— 迟早会漏一次。
    /// 现在同步与异步两条路径都先等、再释,本方法只负责把释放挪离调用线程。
    /// </summary>
    public Task ReleaseAsync()
    {
        return Task.Run(Dispose);
    }

}