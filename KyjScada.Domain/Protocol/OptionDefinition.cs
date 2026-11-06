namespace KyjScada.Domain.Protocol;

/// <summary>协议参数的取值类型。配置界面按此类型自动生成控件。</summary>
public enum OptionType
{
    Text = 0,
    Int = 1,
    Bool = 2,
    Enum = 3
}

/// <summary>
/// 协议特有参数的定义(元数据)。
///
/// 这是「加协议改 1 处」的关键:参数不再是硬编码字段,
/// 而是描述符声明的元数据,配置界面据此自动生成输入框。
/// </summary>
public sealed class OptionDefinition
{
    public string Key { get; }

    public string DisplayName { get; }

    public OptionType Type { get; }

    public string DefaultValue { get; }

    /// <summary>Type 为 Enum 时的候选值。</summary>
    public IReadOnlyList<string> Choices { get; }

    /// <summary>是否必填。导入时缺必填项直接拒绝。</summary>
    public bool Required { get; }

    public OptionDefinition(
        string key,
        string displayName,
        OptionType type = OptionType.Text,
        string defaultValue = "",
        IReadOnlyList<string>? choices = null,
        bool required = false)
    {
        Key = key;
        DisplayName = displayName;
        Type = type;
        DefaultValue = defaultValue;
        Choices = choices ?? Array.Empty<string>();
        Required = required;
    }

    public override string ToString() => Key + "(" + DisplayName + ")";
}
