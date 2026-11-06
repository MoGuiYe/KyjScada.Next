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
    DeviceError = 6
}

/// <summary>一次写入的结果。</summary>
public sealed record WriteOutcome(WriteVerdict Verdict, string Message)
{
    public bool Success => Verdict == WriteVerdict.Ok;

    public static WriteOutcome Ok(string msg) => new(WriteVerdict.Ok, msg);

    public static WriteOutcome Fail(WriteVerdict v, string msg) => new(v, msg);
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
    private readonly Dictionary<string, DateTime> _lastWriteAt = new (StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _owner = new (StringComparer.OrdinalIgnoreCase);
    private readonly List<WriteAudit> _audit = new ();

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
        lock (_lock)
        {
            var hasOwner = _owner.TryGetValue (tagName, out var owner);

            if
            (hasOwner && owner != moduleId)
            {
                return $"写拒绝: 测点 {tagName} 归 {owner} 所有,{moduleId} 无权写。";
            }

            var now = DateTime.Now;
            var hasLast = _lastWriteAt.TryGetValue (tagName, out var last);

            if
            (hasLast)
            {
                var gap = now - last;

                if
                (gap < MinInterval)
                {
                    return $"写拒绝: 测点 {tagName} 距上次写仅 {gap.TotalMilliseconds:0}ms,小于最小间隔 {MinInterval.TotalMilliseconds:0}ms。";
                }
            }

            _lastWriteAt[tagName] = now;
            _audit.Add (new WriteAudit (now, tagName, moduleId, value));

            if
            (_audit.Count > 2000)
            {
                _audit.RemoveRange (0, 500);
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
    public WriteOutcome Write (string varName, double value, string moduleId = "hmi", object? rawValue = null)
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

        // 占用与限速交给已有的 TryEnter(它已经处理了模块互斥与最小间隔)
        var rejected = TryEnter (varName, moduleId, rawValue ?? value);

        if (rejected is not null)
        {
            return WriteOutcome.Fail (WriteVerdict.Rejected, rejected);
        }

        if (Writer is null)
        {
            return WriteOutcome.Fail (WriteVerdict.DeviceError, "没有可用的设备写入器");
        }

        var sent = Writer.WriteVariable (varName, value, out var error);

        if (!sent)
        {
            return WriteOutcome.Fail (WriteVerdict.DeviceError,
                string.IsNullOrEmpty (error) ? "设备写入失败" : error);
        }

        return WriteOutcome.Ok ("已写入 " + varName + " = " + value.ToString ("G6"));
    }
}

public sealed record WriteAudit (DateTime Time, string TagName, string ModuleId, object? Value);
