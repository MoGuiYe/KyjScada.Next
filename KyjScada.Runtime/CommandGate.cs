using KyjScada.Contracts;
using KyjScada.Domain.Config;

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
    NeedsConfirm = 7
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
    /// 权限位(CanJog / CanForce)接进来之后由用户管理控制它。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>实际下发用的写入器。为 null 时 Write 只能校验不能下发。</summary>
    public IDeviceWriter? Writer { get; set; }

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
    public WriteOutcome Write (string varName, double value, string moduleId = "hmi", object? rawValue = null,
        bool confirmed = false)
    {
        if (!Enabled)
        {
            return WriteOutcome.Fail (WriteVerdict.Disabled, "写入未启用(需授权后开启)");
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
        if (!confirmed && ValueGuard is not null)
        {
            WriteCheck check;

            try
            {
                check = ValueGuard (varName, value);
            }
            catch
            {
                // 判定本身出错时按"要确认"处理:宁可多问一句,不可静默下发
                check = new WriteCheck (true, true, new[] { "值判定失败,请人工确认" });
            }

            if (check.NeedsConfirm)
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

        if (Writer is null)
        {
            MarkOutcome (auditId, false);

            return WriteOutcome.Fail (WriteVerdict.DeviceError, "没有可用的设备写入器");
        }

        try
        {
            var sent = Writer.WriteVariable (varName, value, out var error);

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
