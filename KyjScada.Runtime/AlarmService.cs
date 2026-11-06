using KyjScada.Contracts;
using KyjScada.Domain.Alarm;
using KyjScada.Domain.Config;

namespace KyjScada.Runtime;

/// <summary>
/// 报警服务:把配置里的报警规则编译成状态机表,
/// 每次采集后送入快照,产出报警事件与活动报警列表。
///
/// 线程模型:单线程调用(与采集同线程)。App 里由 DispatcherTimer 保证。
///
/// 规则来源:VariableConfig 的 HighAlarm*/LowAlarm*/AlarmDeadband 字段。
/// 高限和低限是两台独立状态机 —— 一个变量可以同时配上限和下限。
///
/// 契约(AlarmEvent/ActiveAlarm)定义在 Contracts,本类只负责产出。
/// </summary>
public sealed class AlarmService
{
    // 一个 (变量, 方向) 一台状态机
    private readonly Dictionary<string, MachineSlot> _machines = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _watchedVars = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>采集线程与界面线程共用本对象,状态机槽位必须互斥访问。</summary>
    private readonly object _sync = new();

    /// <summary>
    /// 全局默认死区。变量自己配了(>0)就用自己的;没配则用这个。
    /// 现场常常整批漏配死区,结果液位在阈值附近反复穿越 → 报警刷屏。
    /// </summary>
    public double DefaultDeadband { get; set; }

    private sealed class MachineSlot
    {
        public required string VarName;
        public required AlarmDirection Direction;
        public required AlarmStateMachine Machine;
        public required double Threshold;
        public string? Note;

        /// <summary>最近一次有效值(供活动报警列表显示当前值)。NaN = 尚无数据。</summary>
        public double LastValue = double.NaN;

        /// <summary>当前这段报警的触发时刻。null = 尚未触发过。</summary>
        public DateTime? TriggeredAt;
    }

    /// <summary>从设备配置编译报警规则。未启用报警的变量不占状态机。</summary>
    public void BuildFrom(DeviceConfig device)
    {
        lock (_sync)
        {
            _machines.Clear();
            _watchedVars.Clear();
        }

        foreach (var group in device.GroupList)
        {
            if (group.VarList is null) continue;

            foreach (var v in group.VarList)
            {
                if (string.IsNullOrEmpty(v.VarName)) continue;

                var hasHigh = v.HighAlarmEnable && float.IsFinite(v.HighAlarmValue);
                var hasLow = v.LowAlarmEnable && float.IsFinite(v.LowAlarmValue);

                if (!hasHigh && !hasLow) continue;

                // 变量自己的死区优先;没配(0)时用全局默认
                var own = v.AlarmDeadband > 0f ? (double)v.AlarmDeadband : 0d;
                var deadband = own > 0d ? own : Math.Max(0d, DefaultDeadband);

                if (hasHigh)
                {
                    _machines[Key(v.VarName, AlarmDirection.High)] = new MachineSlot
                    {
                        VarName = v.VarName,
                        Direction = AlarmDirection.High,
                        Machine = new AlarmStateMachine(AlarmDirection.High, v.HighAlarmValue, deadband),
                        Threshold = v.HighAlarmValue,
                        Note = v.HighAlarmNote
                    };
                }

                if (hasLow)
                {
                    _machines[Key(v.VarName, AlarmDirection.Low)] = new MachineSlot
                    {
                        VarName = v.VarName,
                        Direction = AlarmDirection.Low,
                        Machine = new AlarmStateMachine(AlarmDirection.Low, v.LowAlarmValue, deadband),
                        Threshold = v.LowAlarmValue,
                        Note = v.LowAlarmNote
                    };
                }

                _watchedVars.Add(v.VarName);
            }
        }
    }

    /// <summary>配置了报警规则的变量数(不按方向重复计)。</summary>
    public int WatchedVarCount => _watchedVars.Count;

    /// <summary>状态机总数(变量 × 方向)。</summary>
    public int MachineCount => _machines.Count;

    private static string Key(string varName, AlarmDirection dir)
    {
        return varName + "|" + (dir == AlarmDirection.High ? "H" : "L");
    }

    /// <summary>
    /// 送入一次采集快照,返回本周期发生的全部状态转移。
    /// 断线时快照值为 null,状态机跳过 —— 断线不等于报警恢复。
    /// </summary>
    public List<AlarmEvent> ProcessSnapshot(ValueSnapshot snapshot)
    {
        var events = new List<AlarmEvent>();

        if (snapshot is null || _machines.Count == 0) return events;

        // 多设备并发后,本方法在采集线程跑、GetActiveAlarms 在界面线程跑,
        // 两个线程会碰同一批状态机槽位,所以必须互斥。
        lock (_sync)
        {
            foreach (var slot in _machines.Values)
            {
                var value = snapshot[slot.VarName]?.AsDouble();

                // 跟踪最近有效值;无效帧保持上次值
                if (value is not null) slot.LastValue = value.Value;

                var transition = slot.Machine.Update(value, snapshot.Timestamp);
                if (transition is null) continue;

                if (transition.To == AlarmLevel.Active)
                    slot.TriggeredAt = transition.At;

                events.Add(new AlarmEvent(
                    slot.VarName,
                    slot.Direction,
                    transition.From,
                    transition.To,
                    transition.Value,
                    transition.At));
            }
        }

        return events;
    }

    /// <summary>当前处于 Active 状态的报警。</summary>
    public List<ActiveAlarm> GetActiveAlarms()
    {
        var list = new List<ActiveAlarm>();

        lock (_sync)
        {
            foreach (var slot in _machines.Values)
            {
                if (slot.Machine.Level != AlarmLevel.Active) continue;

                list.Add(new ActiveAlarm(
                    slot.VarName,
                    slot.Direction,
                    slot.Threshold,
                    slot.LastValue,
                    slot.TriggeredAt ?? DateTime.MinValue,
                    slot.Note));
            }
        }

        return list;
    }
}
