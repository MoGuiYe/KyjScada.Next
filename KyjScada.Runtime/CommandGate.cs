using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Domain.Users;

namespace KyjScada.Runtime;

/// <summary>写入被拒绝的原因。</summary>
public enum WriteVerdict
{
    Ok = 0,

    /// <summary>写入总开关没打开。</summary>
    Disabled = 1,

    /// <summary>变量不存在。</summary>
    UnknownVar = 2,

    /// <summary>该变量不是可写点(只读区 / 类型不支持)。</summary>
    NotWritable = 3,

    /// <summary>值非法(NaN/Infinity)。</summary>
    BadValue = 4,

    /// <summary>写得太频繁或被其它模块占用。</summary>
    Rejected = 5,

    /// <summary>设备返回失败。</summary>
    DeviceError = 6,

    /// <summary>值本身没被拒,但和当前状态不一致,需要人工确认后才下发。</summary>
    NeedsConfirm = 7,

    /// <summary>当前账号没有该模块所需的权限(没有 CanWrite/CanJog/CanForce)。</summary>
    Denied = 8
}

/// <summary>一次写入的结果。</summary>
public sealed record WriteOutcome(WriteVerdict Verdict, string Message)
{
    public bool Success => Verdict == WriteVerdict.Ok;

    /// <summary>需要确认时:该值是否越过了组态给的写入上下限(界面据此选更醒目的提示)。</summary>
    public bool OutOfRange { get; init; }

    /// <summary>需要确认时:是否“无法核实”(读不到当前值)。远程写入遇到它必须直接拒。</summary>
    public bool Unverifiable { get; init; }

    /// <summary>需要确认时:远程(MCP)是否必须直接拒绝(值越界或无法核实)。</summary>
    public bool BlocksRemote { get; init; }

    public static WriteOutcome Ok(string msg) => new(WriteVerdict.Ok, msg);

    public static WriteOutcome Fail(WriteVerdict v, string msg) => new(v, msg);

    /// <summary>可以通过,但要先让人确认。**没有任何字节被下发**。</summary>
    public static WriteOutcome Confirm(string msg, bool outOfRange, bool unverifiable = false) =>
        new(WriteVerdict.NeedsConfirm, msg)
        {
            OutOfRange = outOfRange,
            Unverifiable = unverifiable,

            // 远程不能"问一句再发",只能拒或放行:值越界与无法核实必须拒
            BlocksRemote = outOfRange || unverifiable
        };
}

/// <summary>
/// 命令闸门。所有对设备的写操作必须经过这里。
///
/// 这是模块化最大的风险点。原因:
///  模块独立后,HMI、MCP、报警复位、自动控制 四个模块都可能想写同一个线圈。
///  若不加约束,两个模块在同一秒写相反值 -> 设备抖动 -> 现场事故。
///
/// 闸门做四件事:
///  1. 单写者串行化 -- 同一时刻只允许一个写命令在途
///  2. 互锁声明 -- 模块必须声明自己操作哪个设备,冲突直接拒绝
///  3. 速率限制 -- 同一测点最小写间隔(防抖)
///  4. 审计 -- 每次写都记来源模块,事后可查是谁写的
/// </summary>
public sealed class CommandGate
{
    private readonly object _lock = new();
    // 限速用**单调时钟**(TickCount64),时间戳只给人看。
    // 原来用 DateTime.Now 算间隔,机器被 NTP 往回校时之后
    // "现在 - 上次" 会一直是负数,于是该测点被限速锁死,直到系统时间追上那次写入。
    private readonly Dictionary<string, long> _lastWriteAt = new (StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _owner = new (StringComparer.OrdinalIgnoreCase);
    private readonly List<WriteAudit> _audit = new ();

    /// <summary>审计流水号。每条审计一个唯一 id,下发结果按 id 回填,不再"按名字找最近一条"。</summary>
    private long _auditSeq;

    /// <summary>同一测点最小写间隔。默认 500ms。</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromMilliseconds (500);

    /// <summary>
    /// 申请某测点的写权。返回 null 表示成功,返回字符串表示被拒绝的原因。
    /// </summary>
    public string? Claim (string tagName, string moduleId)
    {
        lock (_lock)
        {
            var hasOwner = _owner.TryGetValue (tagName, out var existing);

            return hasOwner && existing != moduleId
                ? $"测点 {tagName} 已被模块 {existing} 占用,{moduleId} 不能同时写。"
                : SetOwner (tagName, moduleId);
        }
    }

    private string? SetOwner (string tagName, string moduleId)
    {
        _owner[tagName] = moduleId;
        return null;
    }

    public void Release (string tagName, string moduleId)
    {
        lock (_lock)
        {
            var hasOwner = _owner.TryGetValue (tagName, out var existing);

            if
            (hasOwner && existing == moduleId)
            {
                _owner.Remove (tagName);
            }
        }
    }

    /// <summary>
    /// 执行一次写。返回 null 表示通过闸门,返回字符串表示被拦。
    /// 调用方在返回 null 后才真正下发到设备。
    /// </summary>
    public string? TryEnter (string tagName, string moduleId, object? value)
    {
        return TryEnter (tagName, moduleId, value, out _);
    }

    /// <summary>
    /// 同 <see cref="TryEnter(string, string, object?)"/>,另外把本次审计的流水号带出来。
    ///
    /// 为什么要返回 id:审计是在**下发之前**落的,下发结果得事后回填。
    /// 早先的回填方式是"从后往前找第一条同名的未结案记录" —— 同一个测点在
    /// MinInterval 之后可以再次进入,两条在途写入一交错,后一条的结果就写到了前一条头上,
    /// 审计表会把失败的记成成功。带 id 就没有歧义。
    /// </summary>
    public string? TryEnter (string tagName, string moduleId, object? value, out long auditId)
    {
        auditId = 0;

        // 权限也必须在这里查:TryEnter 是 public 的,模块可以绕过 WriteAsync 直接调它。
        // 放在 lock 外面是故意的 —— Authorizer 是外部注入的委托,
        // 不在持锁时调用外部代码。
        var noPermission = CheckPermission (moduleId);

        if (noPermission is not null) return noPermission;

        lock (_lock)
        {
            // 总开关也要在这里查:模块可以绕过 Write 直接调 TryEnter,
            // 不查的话"没授权也能下发"。
            if (!Enabled)
            {
                return "写拒绝: 写入未启用(需授权后开启)";
            }

            var hasOwner = _owner.TryGetValue (tagName, out var owner);

            if
            (hasOwner && owner != moduleId)
            {
                return $"写拒绝: 测点 {tagName} 归 {owner} 所有,{moduleId} 无权写。";
            }

            var now = DateTime.Now;
            var tick = Environment.TickCount64;
            var hasLast = _lastWriteAt.TryGetValue (tagName, out var lastTick);

            if
            (hasLast)
            {
                var gapMs = tick - lastTick;

                if
                (gapMs < (long)MinInterval.TotalMilliseconds)
                {
                    return $"写拒绝: 测点 {tagName} 距上次写仅 {gapMs}ms,小于最小间隔 {MinInterval.TotalMilliseconds:0}ms。";
                }
            }

            _lastWriteAt[tagName] = tick;

            auditId = ++_auditSeq;
            _audit.Add (new WriteAudit (now, tagName, moduleId, value, null, auditId, tick));

            if

            (_audit.Count > 2000)

            {

                TrimSettled ();

            }

            return null;
        }
    }

    public IReadOnlyList<WriteAudit> Audit
    {
        get
        {
            lock (_lock) return _audit.ToList ();
        }
    }

    public IReadOnlyDictionary<string, string> Owners
    {
        get
        {
            lock (_lock) return new Dictionary<string, string> (_owner);
        }
    }

    // ==================== 写入准入 + 实际下发 ====================

    /// <summary>
    /// 写入总开关。**默认关闭**:没拿到授权前不应该能写设备。
    /// 它只是"人有没有把下发总闸门打开",不是权限判定 —— 权限见 <see cref="Authorizer"/>。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 权限判定入口(由界面/宿主注入)。
    ///
    /// 为 null 表示"这次没有用户体系"(单元测试、无登录的嵌入场景),此时只受
    /// <see cref="Enabled"/> 约束 —— 与 ValueGuard 同一口径。
    ///
    /// 注入之后**每一个**下发入口都要过这里,而不是靠各页面自己记得判:
    /// 写入口有六个(参数设置数值行 / Bool 按钮 / 点动 / 急停 / 报警复位 / MCP 远程写),
    /// 少判一处就是一个"没有权限也能让设备动作"的洞。之前确实如此:
    /// 界面只判了 CanWrite、急停只判 CanJog、MCP 通道根本不看登录态,
    /// 而 CanForce 在全仓没有一处校验。
    /// </summary>
    public Func<PermissionBit, bool>? Authorizer { get; set; }

    /// <summary>
    /// 模块 → 该模块下发所需的权限位。
    ///
    /// 为什么按模块分级:写"设定值"、点动、急停/强制写的危险程度完全不同。
    /// 急停是整片写 0、强制写绕过逻辑,都必须 CanForce(只给管理员);
    /// 没登记的模块一律按最常规的 CanWrite 处理,不给自己开特权。
    /// </summary>
    public Dictionary<string, PermissionBit> ModulePermissions { get; } =
        new (StringComparer.OrdinalIgnoreCase)
        {
            ["hmi"] = PermissionBit.CanWrite,
            ["mcp"] = PermissionBit.CanWrite,
            ["jog"] = PermissionBit.CanJog,
            ["estop"] = PermissionBit.CanForce,
            ["force"] = PermissionBit.CanForce
        };

    /// <summary>该模块有没有下发权限。返回 null = 通过,否则是拒绝原因。</summary>
    private string? CheckPermission (string? moduleId)
    {
        var authorizer = Authorizer;

        // 没有用户体系:不判(单测/嵌入场景行为与以前一致)
        if (authorizer is null) return null;

        var need = moduleId is not null && ModulePermissions.TryGetValue (moduleId, out var bit)
            ? bit
            : PermissionBit.CanWrite;

        bool ok;

        try
        {
            ok = authorizer (need);
        }
        catch
        {
            // 判定本身出错按"无权限"处理:宁可拒发,不可静默下发
            ok = false;
        }

        return ok
            ? null
            : "写拒绝:模块 " + moduleId + " 需要「" + PermissionNames.Of (need) + "」权限,当前账号没有。";
    }

    /// <summary>实际下发用的写入器。为 null 时 Write 只能校验不能下发。</summary>
    /// <summary>
    /// 设备写入器。**必须 volatile**:
    /// 界面线程在换设备时写它,线程池上的采集/写入线程在读它,
    /// 普通属性没有内存屏障,读侧可能长期看到旧值(已释放的服务)。
    /// </summary>
    private volatile IDeviceWriter? _writer;

    /// <summary>
    /// 接上/摘掉设备写入器。**只改引用,不清故障标记**。
    ///
    /// 早先这里"接到任意非 null 值就 ClearWriteStuck",于是换设备/重读组态的建页路径
    /// (BuildWritePage 无条件 _gate.Writer = _svc as IDeviceWriter)只要在重建完成前
    /// 跑一次,就会把**仍然卡死的旧服务**重新挂回来,顺手抹掉"写入卡死"标记 ——
    /// 自愈被无声抵消,现场表现依旧是"写什么都等十秒然后失败"。
    ///
    /// 故障标记只由 <see cref="WriterConfirmed"/> 清除:调用方必须**亲眼确认**
    /// 手里这个写入器是这一次刚建出来的新连接(重建成功)才行。
    ///
    /// 反过来也有个陷阱:卡死后该字段为空,若这里"值非 null 就一律接上",
    /// 建页路径就会把已作废的那条旧服务重新接回来 —— 那一帧会直接打到
    /// 一个**没人采、句柄已关**的连接上。所以:
    ///   · 已因卡死摘掉 Writer 之后,<see cref="WriterConfirmed"/> 是**唯一**入口,
    ///     这里接到非 null 一律**拒收并保持 null**(写入继续立刻失败,等重建);
    ///   · 没有卡死时,setter 只做换引用(释放旧嫁接、接新来的)。
    /// 把门禁放在闸门里而不是靠每个调用方记得判 WriteStuck ——
    /// 建页路径有六处写入口,漏一处就是把卡死的服务接回去。
    /// </summary>
    public IDeviceWriter? Writer
    {
        get => _writer;

        set
        {
            // 卡死状态下任何人拿普通 setter 重新挂上来都不足信(它没经过重建成功)。
            // 只允许"摘掉"(null):否则保留原状,免得 BuildWritePage 把废服务挂回。
            if (WriteStuck && value is not null) return;

            _writer = value;
        }
    }

    /// <summary>
    /// 接上一个**已确认是重建产物**的写入器:置引用并清掉"写入卡死"标记。
    ///
    /// 只有重建成功的路径该调它 —— 界面/宿主知道这个实例是这一次新建出来的,
    /// 而不是台账里那条卡死的旧流水线。值为 null 时等同于 <see cref="Writer"/> 置空
    /// (重建失败也就谈不上"已排除故障",标记保留)。
    /// </summary>
    public void WriterConfirmed (IDeviceWriter? writer)
    {
        _writer = writer;

        if (writer is not null) ClearWriteStuck ();
    }

    /// <summary>
    /// 在途写入计数。释放设备前先等它归零,避免"关串口"和"正在发的那一帧"
    /// 抢同一个句柄(表现为偶发的写入异常或串口状态错乱)。
    ///
    /// **不是 readonly**:判定"这一帧已经卡死"之后必须换成一把新的,
    /// 否则卡死的那一帧永远不 Release,这把信号量会被永久占用 ——
    /// 之后每一次写都要等满 WriteSendTimeout 才失败(见 DeclareWriteStuck)。
    /// </summary>
    private SemaphoreSlim _inFlight = new (1, 1);

    /// <summary>
    /// 是否已判定"写入卡死"(0=正常,1=已判定)。用 int 是为了能原子地"只报一次"。
    /// 卡死意味着**设备句柄已经不可信**,只有重建连接才可能恢复。
    /// </summary>
    private int _writeStuck;

    /// <summary>写入是否已判定卡死(界面/看门狗据此提示与重建)。</summary>
    public bool WriteStuck => Volatile.Read (ref _writeStuck) != 0;

    /// <summary>判定卡死的时刻(诊断用)。</summary>
    public DateTime? WriteStuckAt { get; private set; }

    /// <summary>判定卡死的原因(诊断用)。</summary>
    public string? WriteStuckReason { get; private set; }

    /// <summary>
    /// 判定"写入卡死"时触发(参数是原因)。宿主**必须**据此重建该设备的连接:
    /// 卡在同步往返里的那一帧只有关掉句柄才能打断,闸门自己做不到。
    /// 回调在写入线程上同步触发,**不要**在里面做耗时动作。
    /// </summary>
    public Action<string>? WriteStuckDetected { get; set; }

    /// <summary>
    /// 等待"在途写入"结束的上限。串口/TCP 单次往返通常几百毫秒,
    /// 这里给足余量(含断线重试),但要有个头 —— 否则一次卡死的写会永久堵住所有写入。
    /// </summary>
    public TimeSpan WriteSendTimeout { get; set; } = TimeSpan.FromSeconds (10);

    /// <summary>等所有在途写入结束(最多等 timeout)。返回 false = 超时。</summary>
    public async Task<bool> WaitForWritesAsync (TimeSpan timeout)
    {
        // 先取到局部:判定卡死会换掉 _inFlight,两次读之间被换掉的话
        // 会去等一把已经作废的信号量(白等满超时)。
        var inFlight = _inFlight;

        if (!await inFlight.WaitAsync (timeout).ConfigureAwait (true)) return false;

        inFlight.Release ();

        return true;
    }

    /// <summary>
    /// 判定"上一次写入已经卡死",并做能自愈的准备。
    ///
    /// 为什么不能只把本次调用判失败:WriteVariable 是同步阻塞调用(串口/TCP 往返),
    /// 卡住的那一帧**永不返回**,于是 _inFlight 被永久占住 ——
    ///   · 之后每一次写都要等满 WriteSendTimeout(10 秒)才失败,
    ///     现场表现是"程序还活着,但写什么都要等十秒然后失败";
    ///   · WaitForWritesAsync 每次都超时,释放流水线永远等不到"写完了";
    ///   · Writer 还指着那个卡死的服务,下次写继续卡。
    /// 没有自愈,就只能重启程序。
    ///
    /// 这里做三件事:
    ///   1. 摘掉 Writer:后续写入立刻得到"没有可用的设备写入器",
    ///      不再陪那个卡死的帧一起等满超时;
    ///   2. 换一把新的在途信号量:卡死的那一帧永远不会 Release 旧的那把,
    ///      换掉它单写者才能重新工作(旧的那把留给那个线程自己收尾,不 Dispose,
    ///      免得它稍后回来 Release 时撞 ObjectDisposedException);
    ///   3. 标记闸门故障并通知宿主重建连接 —— 关句柄是唯一能打断阻塞往返的手段。
    /// </summary>
    private void DeclareWriteStuck (string varName, SemaphoreSlim held, TimeSpan waited)
    {
        // 1. 摘 Writer(直接写字段:走属性会被"接上新写入器"的解释带偏,这里根本不是重建)
        _writer = null;

        // 2. 换信号量。CompareExchange 保证只有还看着**这一把**的线程才换,
        //    避免多个超时线程各换各的,把"单写者串行化"拆掉。
        Interlocked.CompareExchange (ref _inFlight, new SemaphoreSlim (1, 1), held);

        // 3. 只报一次:否则卡死期间每一次写都会再触发一次重建
        if (Interlocked.Exchange (ref _writeStuck, 1) != 0) return;

        WriteStuckAt = DateTime.Now;

        WriteStuckReason = "写入 " + varName + " 超过 "
            + waited.TotalSeconds.ToString ("0.#") + " 秒未返回,判定该设备写入已卡死";

        try
        {
            WriteStuckDetected?.Invoke (WriteStuckReason);
        }
        catch
        {
            // 宿主回调出错不能反过来影响写路径(它已经在故障处理里了)
        }
    }

    /// <summary>清除"写入卡死"标记。接上新写入器时自动调用(见 <see cref="Writer"/>)。</summary>
    private void ClearWriteStuck ()
    {
        Interlocked.Exchange (ref _writeStuck, 0);

        WriteStuckAt = null;
        WriteStuckReason = null;
    }

    /// <summary>
    /// 下发前的值判定:返回非 null 表示"要先确认",闸门就**一个字节都不发**。
    ///
    /// 为什么放在闸门而不是各页面:写入口有六处(界面数值行、Bool 按钮、点动、急停、
    /// 报警复位、MCP 远程写)。安全逻辑放页面里,新加一个入口就漏一次 ——
    /// 之前 MCP 就是直接 Write,一句 write_variable("X", 5000) 直发 PLC。
    /// 放在这里,凡是 Write 都必须过这道关。
    ///
    /// 默认 null = 不做值判定(单测里构造的闸门、以及没有组态的场景行为不变)。
    /// </summary>
    public Func<string, double, WriteCheck>? ValueGuard { get; set; }

    private readonly Dictionary<string, bool> _writable = new (StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _reason = new (StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 编译可写点清单。是否可写由调用方判定(区是否可写、类型是否支持),
    /// 闸门不猜协议细节 —— 协议差异留在实现层。
    /// </summary>
    public void BuildWritables (
        DeviceConfig device,
        Func<VariableConfig, bool> isWritable,
        Func<VariableConfig, string>? reasonOf = null)
    {
        lock (_lock)
        {
            _writable.Clear ();
            _reason.Clear ();

            if (device?.GroupList is null) return;

            foreach (var g in device.GroupList)
            {
                if (g.VarList is null) continue;

                foreach (var v in g.VarList)
                {
                    if (string.IsNullOrWhiteSpace (v.VarName)) continue;

                    var ok = isWritable (v);

                    _writable[v.VarName] = ok;

                    if (!ok) _reason[v.VarName] = reasonOf?.Invoke (v) ?? "该点只读";
                }
            }
        }
    }

    public int WritableCount
    {
        get
        {
            lock (_lock) return _writable.Count (kv => kv.Value);
        }
    }

    public bool IsWritable (string tagName)
    {
        if (string.IsNullOrEmpty (tagName)) return false;

        lock (_lock)
        {
            return _writable.TryGetValue (tagName, out var ok) && ok;
        }
    }

    public string ReasonOf (string tagName)
    {
        lock (_lock)
        {
            return _reason.TryGetValue (tagName, out var r) ? r : "未配置的可写点";
        }
    }

    /// <summary>
    /// 一次完整写入:开关 → 可写性 → 值合法性 → 占用/限速 → 真正下发。
    /// **所有校验都在下发之前完成**,任何一步不过就一个字节都不发。
    /// </summary>
    /// <param name="confirmed">
    /// true = 调用方已经拿 <see cref="ValueGuard"/> 的结论问过人了(或本身不需要问)。
    /// 只该在"确认框已经弹过、用户点了是"之后传 true。
    /// </param>
    public async Task<WriteOutcome> WriteAsync (string varName, double value, string moduleId = "hmi",
        object? rawValue = null, bool confirmed = false)
    {
        if (!Enabled)
        {
            return WriteOutcome.Fail (WriteVerdict.Disabled, "写入未启用(需授权后开启)");
        }

        // 权限先于其它一切判定:没权限的人连"值是否可疑"都不该被问到
        var noPermission = CheckPermission (moduleId);

        if (noPermission is not null)
        {
            return WriteOutcome.Fail (WriteVerdict.Denied, noPermission);
        }

        if (string.IsNullOrWhiteSpace (varName))
        {
            return WriteOutcome.Fail (WriteVerdict.UnknownVar, "变量名为空");
        }

        bool known;
        bool writable;

        lock (_lock)
        {
            known = _writable.TryGetValue (varName, out writable);
        }

        if (!known)
        {
            return WriteOutcome.Fail (WriteVerdict.UnknownVar, "配置里没有变量 " + varName);
        }

        if (!writable)
        {
            return WriteOutcome.Fail (WriteVerdict.NotWritable, ReasonOf (varName));
        }

        if (double.IsNaN (value) || double.IsInfinity (value))
        {
            return WriteOutcome.Fail (WriteVerdict.BadValue, "值非法(NaN/Infinity)");
        }

        // 值判定放在占用/限速之前:被要求确认的写入不该烧掉限速名额,也不该落审计条目
        // —— 它从来没发出去过。
        //
        // **值判定永远要跑**,confirmed 只豁免"普通差异确认"这一种情况。
        // 早先写成 `if (!confirmed && ...)` 整个跳过 —— 于是急停/点动/Bool 按钮
        // 传 confirmed:true 就等于关掉了全部校验:断线(当前值不可信)、越界
        // 都会零校验直发 PLC。这与"Bool 也要查不可核实"的设计完全相反。
        if (ValueGuard is not null)
        {
            WriteCheck check;

            try
            {
                check = ValueGuard (varName, value);
            }
            catch
            {
                // 判定本身出错时按"危险且不可核实"处理:宁可拒发,不可静默下发
                check = new WriteCheck (true, true, new[] { "值判定失败,请人工确认" })
                    { Unverifiable = true };
            }

            // 两类**任何情况都不放过**:
            //   · OutOfRange   —— 值本身危险,哪都不能发
            //   · Unverifiable —— 读不到当前状态,没人能担保这次下发是对的
            // 其余(值与当前不同的普通差异)由 confirmed 表示"人已经确认过了"。
            if (check.NeedsConfirm && (!confirmed || check.BlocksRemote))
            {
                return WriteOutcome.Confirm (string.Join ("; ", check.Reasons), check.OutOfRange, check.Unverifiable);
            }
        }

        // 占用与限速交给已有的 TryEnter(它已经处理了模块互斥与最小间隔)
        // 同时拿到审计 id:下发结果按 id 回填,并发写同一个测点时不会标错条目
        var rejected = TryEnter (varName, moduleId, rawValue ?? value, out var auditId);

        if (rejected is not null)
        {
            return WriteOutcome.Fail (WriteVerdict.Rejected, rejected);
        }

        // 只读一次 Writer 到局部变量:它是 volatile,界面线程换设备时会置 null,
        // 读两次之间被清掉的话,下面的判空通过、真正调用时却是空引用
        // (会被 catch 记成"设备写入失败",但真实原因是设备刚被释放,信息误导)。
        var writer = Writer;

        if (writer is null)
        {
            MarkOutcome (auditId, false);

            return WriteOutcome.Fail (WriteVerdict.DeviceError, "没有可用的设备写入器");
        }

        // 真正发帧的这段必须与"释放设备"互斥:关串口与在途帧抢同一句柄。
        //
        // **必须带超时**:Writer.WriteVariable 是同步阻塞调用(串口/TCP 往返),
        // 驱动异常时可能永不返回。无超时地等这把信号量,一次卡死就会让之后
        // **所有**写入(界面/点动/急停/MCP)永久排队,HTTP 请求线程被逐个占满,
        // 现场表现为"程序还活着但什么都写不进去"。
        //
        // 超时不只是"本次失败":那意味着**上一次写入是真的卡死了**(正常往返几百毫秒),
        // 所以还要宣告故障并让宿主重建连接 —— 否则每一次写都白等满超时,永不自愈。
        // 先取到局部:宣告故障时会换掉 _inFlight,释放时必须还给我们真正持有的那一把。
        var inFlight = _inFlight;

        if (!await inFlight.WaitAsync (WriteSendTimeout).ConfigureAwait (false))
        {
            MarkOutcome (auditId, false);

            DeclareWriteStuck (varName, inFlight, WriteSendTimeout);

            return WriteOutcome.Fail (WriteVerdict.DeviceError,
                "上一帧写入迟迟未返回(超过 " + WriteSendTimeout.TotalSeconds.ToString ("0.#")
                + " 秒),已判定该设备写入卡死,本次未下发。已停止向该连接下发并触发重建,请稍候重试。");
        }

        try
        {
            var sent = writer.WriteVariable (varName, value, out var error);

            if (!sent)
            {
                MarkOutcome (auditId, false);

                return WriteOutcome.Fail (WriteVerdict.DeviceError,
                    string.IsNullOrEmpty (error) ? "设备写入失败" : error);
            }

            MarkOutcome (auditId, true);

            return WriteOutcome.Ok ("已写入 " + varName + " = " + value.ToString ("G6"));
        }
        catch
        {
            // 写入器抛异常时也要结案:否则这条审计永远挂着 Outcome=null,
            // 事后看审计既不像成功也不像失败,查不出到底下发没有。
            MarkOutcome (auditId, false);

            throw;
        }
        finally
        {
            // 必须释放:漏掉一次就等于永久的"有在途写入",释放设备会一直等到超时。
            // 释放的是**本次真正持有的那一把**(卡死换过信号量时,旧的那把不归我们管)。
            inFlight.Release ();
        }
    }

    /// <summary>
    /// 同步版写入。仅供不方便改异步的调用点(单测、退出路径)使用。
    ///
    /// **不要在界面线程上调用**:内部可能等一次完整的设备往返(断线时数秒)。
    /// </summary>
    public WriteOutcome Write (string varName, double value, string moduleId = "hmi", object? rawValue = null,
        bool confirmed = false)
    {
        return WriteAsync (varName, value, moduleId, rawValue, confirmed).GetAwaiter ().GetResult ();
    }

    /// <summary>
    /// 给指定审计补上"实际结果"。按流水号定位,不按名字匹配 ——
    /// 名字匹配在并发同名写入时会标错条目,而且大小写敏感(配置里写成 kyJ1_Start
    /// 就找不到 KYJ1_Start 那条,审计永远悬空)。
    /// </summary>
    /// <summary>
    /// 裁剪审计表:只删**已结案**的记录。
    ///
    /// 早先是无条件 RemoveRange(0,500),而审计在**下发之前**就落了记录 ——
    /// 被裁掉的很可能正是在途的那几条;等下发结果回来,MarkOutcome 按 id 找不到记录,
    /// RevertRateLimit 也就不会执行,于是"设备写失败"被记成一次成功的写,
    /// 之后正常的重试还会被最小间隔挡住。
    /// 在途记录最多 MaxConcurrent(每个写入线程一条),永远留得下。
    /// </summary>
    private void TrimSettled ()
    {
        var settleable = _audit.Count (a => a.Outcome is not null);

        if (settleable == 0) return;

        var drop = Math.Min (settleable, 500);

        _audit.RemoveAll (a => a.Outcome is not null && drop-- > 0);
    }
    public void MarkOutcome (long auditId, bool success)
    {
        if (auditId <= 0) return;

        lock (_lock)
        {
            for (var i = _audit.Count - 1; i >= 0; i--)
            {
                if (_audit[i].Id == auditId)
                {
                    var record = _audit[i];

                    _audit[i] = record with { Outcome = success ? "ok" : "fail" };

                    // 失败要把限速时间戳撤掉:TryEnter 在下发之前就记了"刚写过",
                    // 不撤的话现场会遇到"设备报错 → 立刻重试 → 提示距上次写仅 xx ms 被拒绝",
                    // 一个纯粹的通讯失败被当成操作太频繁。
                    if (!success) RevertRateLimit (record.TagName, record.RateTick);

                    return;
                }
            }
        }
    }

    /// <summary>
    /// 撤掉失败那次写入占用的限速名额。
    /// 只有当时间戳**仍是这次失败尝试的时间**时才撤 —— 这期间若已有另一次成功写入
    /// 把它更新过,撤掉就等于把合法的限速放开了。
    /// </summary>
    private void RevertRateLimit (string tagName, long attemptTick)
    {
        if (_lastWriteAt.TryGetValue (tagName, out var last) && last == attemptTick)
        {
            _lastWriteAt.Remove (tagName);
        }
    }
}

/// <param name="RateTick">本次写入占用的限速名额标识(单调时钟毫秒)。失败回滚时用它精确比对,
/// 不用 DateTime —— 与限速判定保持同一时间源。</param>
public sealed record WriteAudit (
    DateTime Time, string TagName, string ModuleId, object? Value, string? Outcome = null, long Id = 0, long RateTick = 0);
