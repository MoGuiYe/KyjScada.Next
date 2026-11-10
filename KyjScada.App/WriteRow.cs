using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KyjScada.App;

/// <summary>参数设置页一行:一个可写点。</summary>
public sealed class WriteRow : INotifyPropertyChanged
{
    public string GroupName { get; set; } = "";
    public string VarName { get; set; } = "";
    public string Unit { get; set; } = "";

    /// <summary>组态里的中文描述。现场变量名多是英文缩写(LQB1_Fre_Set),
    /// 不显示备注就得靠背,选错点写到别的量上就是事故。</summary>
    public string Comments { get; set; } = "";

    /// <summary>
    /// 这一行是 Bool。Bool 在本页不用数字输入框,直接给"写1(开)/写0(关)"两个按钮:
    /// 布尔量填浮点数既反直觉,也容易把 0.5 这种值发给设备。
    /// </summary>
    public bool IsBool { get; set; }

    /// <summary>非 Bool 行才显示数值输入框(供 XAML 用 BooleanToVisibility 绑)。</summary>
    public bool IsNumeric => !IsBool;

    /// <summary>
    /// 读屏用的按钮名。
    ///
    /// 不用 XAML 里的 StringFormat 拼:变量名里带下划线(LQB1_Start),
    /// WPF 会把按钮 Content 里的下划线当访问键前缀吃掉,读屏念成 "LQB1Start"。
    /// 预计算成普通字符串就没有这个歧义。
    /// </summary>
    public string A11yNameOn => "写1(开): " + A11yVar;

    public string A11yNameOff => "写0(关): " + A11yVar;

    public string A11yNameWrite => "写入: " + A11yVar;

    public string A11yNameInput => "写入值: " + VarName;

    /// <summary>
    /// 读屏名里的变量名:下划线要写成双下划线。
    ///
    /// WPF 把可访问名里的 "_" 当下划线访问键标记并在朗读时去掉,
    /// 于是 LQB1_Start 被念成 "LQB1Start" —— 和界面上写的、配置里存的都对不上,
    /// 读屏用户按名字核对点位会核错。双写是 WPF 的标准转义,念出来正好是一个下划线。
    /// </summary>
    private string A11yVar => (VarName ?? "").Replace ("_", "__");

    /// <summary>当前实测值(只读显示,由总线刷新)。</summary>
    private string _currentText = "--";

    public string CurrentText
    {
        get => _currentText;
        set
        {
            if (_currentText == value) return;

            _currentText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>待写入值(用户输入)。</summary>
    public string InputText { get; set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}