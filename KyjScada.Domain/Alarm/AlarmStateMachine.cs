using KyjScada.Contracts;

namespace KyjScada.Domain.Alarm;

/// <summary>状态机输出:一次状态转移。</summary>
public sealed record AlarmTransition(
    AlarmDirection Direction,
    AlarmLevel From,
    AlarmLevel To,
    double Value,
    DateTime At);

/// <summary>
/// 单变量单方向的报警状态机。**带死区**。
///
/// 为什么必须用显式状态位而不是靠上一帧值推导:
///  老项目在这上面栽过两次,两个推导方案互斥且都不对:
///
///   方案A:用 last >= set 判断已触发
///     失效:过程量从死区【下沿】退出时,last 会掉到 set 以下,
///           状态被忘记,消除事件永远等不到 -> 报警永久挂起。
///
///   方案B:改用 last >= set - deadband 判断
///     失效:死区内的值让状态一直是 true,
///           于是 99.5 -> 100.5 这种正常上升沿反而触发不了。
///
/// 只有独立状态位能同时做对两件事。
///
/// 死区语义(以上限为例):
///   值 > 上限              -> 触发
///   值 < 上限 - 死区        -> 消除
///   值在 [上限-死区, 上限]  -> 保持原状态(不抖动)
///
/// 实测背景:老系统液位报警触发 181 次、消除 383 次,
/// 因为液位在阈值 54 附近反复穿越,每次穿越都产生一对事件。
/// </summary>
public sealed class AlarmStateMachine
{
    private readonly AlarmDirection _direction;
    private readonly double _threshold;
    private readonly double _deadband;

    private AlarmLevel _level = AlarmLevel.Normal;

    public AlarmDirection Direction => _direction;

    public double Threshold => _threshold;

    public double Deadband => _deadband;

    public AlarmLevel Level => _level;

    public AlarmStateMachine (AlarmDirection direction, double threshold, double deadband = 0d)
    {
        if (double.IsNaN(threshold) || double.IsInfinity(threshold))
        {
            throw new ArgumentException("报警阈值必须是有限数值", nameof(threshold));
        }

        if (deadband < 0d)
        {
            throw new ArgumentException("死区不能为负", nameof(deadband));
        }

        _direction = direction;
        _threshold = threshold;
        _deadband = deadband;
    }

    /// <summary>
    /// 送入一个采样值,返回本次发生的状态转移(无转移返回 null)。
    /// </summary>
    /// <param name="value">采样值。null 表示无效,此时不改变状态。</param>
    /// <param name="at">采样时刻。</param>
    public AlarmTransition? Update (double? value, DateTime at)
    {
        // 无效值不参与判断。断线不等于正常,也不等于报警。
        if (value is null)
        {
            return null;
        }

        var v = value.Value;

        if (double.IsNaN(v) || double.IsInfinity(v))
        {
            return null;
        }

        var next = ComputeNextLevel(v);

        if (next == _level)
        {
            return null;
        }

        var transition = new AlarmTransition(_direction, _level, next, v, at);

        _level = next;

        return transition;
    }

    /// <summary>根据当前值与状态计算下一状态(含死区滞回)。</summary>
    private AlarmLevel ComputeNextLevel (double value)
    {
        if (_direction == AlarmDirection.High)
        {
            if (_level == AlarmLevel.Normal)
            {
                // 未报警:必须超过阈值才触发
                return value > _threshold ? AlarmLevel.Active : AlarmLevel.Normal;
            }

            // 已报警:必须降回 阈值-死区 以下才消除
            var clearLine = _threshold - _deadband;
            return value < clearLine ? AlarmLevel.Normal : AlarmLevel.Active;
        }

        // 低报警
        if (_level == AlarmLevel.Normal)
        {
            // 未报警:必须低于阈值才触发
            return value < _threshold ? AlarmLevel.Active : AlarmLevel.Normal;
        }

        // 已报警:必须升回 阈值+死区 以上才消除
        var clearLineLow = _threshold + _deadband;
        return value > clearLineLow ? AlarmLevel.Normal : AlarmLevel.Active;
    }

    /// <summary>强制复位到正常(操作员确认用)。</summary>
    public AlarmTransition? Reset (DateTime at)
    {
        if (_level == AlarmLevel.Normal)
        {
            return null;
        }

        var transition = new AlarmTransition(_direction, _level, AlarmLevel.Normal, double.NaN, at);

        _level = AlarmLevel.Normal;

        return transition;
    }
}
