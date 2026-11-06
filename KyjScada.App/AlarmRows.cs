namespace KyjScada.App;

/// <summary>当前报警列表一行(实时来自 AlarmService)。</summary>
public sealed class ActiveAlarmRow
{
    public string VarName { get; set; } = "";
    public string DirectionText { get; set; } = "";
    public string ValueText { get; set; } = "";
    public string ThresholdText { get; set; } = "";
    public string DurationText { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>历史报警表格一行(来自 AlarmRepository)。</summary>
public sealed class AlarmHistoryRow
{
    /// <summary>数据库主键,确认时要用。</summary>
    public long Id { get; set; }

    public string TimeText { get; set; } = "";
    public string VarName { get; set; } = "";
    public string TypeText { get; set; } = "";
    public string DirectionText { get; set; } = "";
    public string ValueText { get; set; } = "";
    public string AckText { get; set; } = "";
}