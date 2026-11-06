using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KyjScada.App;

/// <summary>
/// 界面一行。纯展示模型,不含业务逻辑。
///
/// **必须实现 INotifyPropertyChanged**:以前是纯属性、靠界面定时器 500 ms 调一次
/// ValueGrid.Items.Refresh() 全表重画。139 行的表,一次重画实测 80~160 ms,
/// 也就是每半秒界面线程被占掉一截 —— 现场看到的就是"卡顿"。
/// 有了通知,WPF 只重画真正改了的单元格。
/// </summary>
public sealed class ValueRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _valueText = "--";
    private string _qualityText = "";
    private string _timeText = "";

    public string Name { get; set; } = "";

    /// <summary>原始类型(Bool/Byte/Short/Int/Float/Double...)。决定显示格式。</summary>
    public string VarType { get; set; } = "";

    /// <summary>显示文本。无数据时显示 "--" 而不是 0。</summary>
    public string ValueText
    {
        get => _valueText;
        set => Set (ref _valueText, value);
    }

    public string Unit { get; set; } = "";

    public string QualityText
    {
        get => _qualityText;
        set => Set (ref _qualityText, value);
    }

    public string TimeText
    {
        get => _timeText;
        set => Set (ref _timeText, value);
    }

    /// <summary>值没变就不通知:139 行 × 2 Hz = 每秒 278 次无谓重绘,白烧界面线程。</summary>
    private void Set (ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (string.Equals (field, value, StringComparison.Ordinal)) return;

        field = value;

        PropertyChanged?.Invoke (this, new PropertyChangedEventArgs (name));
    }
}