using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KyjScada.App;

/// <summary>参数设置页一行:一个可写点。</summary>
public sealed class WriteRow : INotifyPropertyChanged
{
    public string GroupName { get; set; } = "";
    public string VarName { get; set; } = "";
    public string Unit { get; set; } = "";

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