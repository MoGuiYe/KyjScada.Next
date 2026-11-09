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