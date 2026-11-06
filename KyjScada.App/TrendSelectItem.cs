using System.Windows.Media;

namespace KyjScada.App;

/// <summary>趋势页左侧变量列表项。带颜色,和曲线颜色对应。</summary>
public sealed class TrendSelectItem
{
    public string Name { get; set; } = "";

    /// <summary>曲线颜色。选中后画图用同一个颜色。</summary>
    public Color Color { get; set; }

    /// <summary>单位,显示在读数和统计里。</summary>
    public string Unit { get; set; } = "";
}

/// <summary>光标读数显示项。</summary>
public sealed class CursorReadout
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public Color Color { get; set; }
}

/// <summary>报警事件列表一行。点击后把光标定位到该时刻。</summary>
public sealed class TrendEventRow
{
    public DateTime At { get; set; }

    /// <summary>时分秒,列表里显示。</summary>
    public string TimeText { get; set; } = "";

    public string Text { get; set; } = "";
}
