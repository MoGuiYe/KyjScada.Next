namespace KyjScada.Contracts;

/// <summary>报警方向。基础枚举,放 Contracts 供各层共用(避免各层各定义一份)。</summary>
public enum AlarmDirection
{
    High = 0,
    Low = 1
}

/// <summary>报警状态。</summary>
public enum AlarmLevel
{
    /// <summary>正常。</summary>
    Normal = 0,

    /// <summary>已触发。</summary>
    Active = 1
}

/// <summary>
/// 报警事件(触发或消除)。跨层契约:Runtime 产出,Infrastructure 落库,App 显示。
/// 放在 Contracts 而不是 Runtime —— 否则 Infrastructure 引用 Runtime 会成环。
/// </summary>
public sealed record AlarmEvent(
    string VarName,
    AlarmDirection Direction,
    AlarmLevel From,
    AlarmLevel To,
    double Value,
    DateTime At)
{
    /// <summary>是否为触发事件。</summary>
    public bool IsTrigger => To == AlarmLevel.Active;

    /// <summary>方向中文。</summary>
    public string DirectionText => Direction == AlarmDirection.High ? "上限" : "下限";

    /// <summary>事件类型中文。</summary>
    public string TypeText => IsTrigger ? "触发" : "消除";
}

/// <summary>当前处于活动状态的报警(界面显示用)。</summary>
public sealed record ActiveAlarm(
    string VarName,
    AlarmDirection Direction,
    double Threshold,
    double Value,
    DateTime TriggeredAt,
    string? Note)
{
    /// <summary>方向中文。</summary>
    public string DirectionText => Direction == AlarmDirection.High ? "上限" : "下限";
}
