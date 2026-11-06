using KyjScada.Domain.Config;

namespace KyjScada.Domain.Protocol;

/// <summary>
/// 协议描述符。**加一种协议 = 写一个实现类 + 注册一行**。
///
/// 对比老方案(thinger)加协议要改 6 处:
///  1. Device/XxxDevice.cs 建类          -> 不需要,统一 DeviceConfig
///  2. Group/XxxGroup.cs 建类            -> 不需要,统一 GroupConfig
///  3. Variable/XxxVariable.cs 建类      -> 不需要,统一 VariableConfig
///  4. Project.cs 加 List 属性           -> 不需要,用字典
///  5. ConfigImporter 加键名+foreach     -> 不需要,按 Key 分派
///  6. ConfigManage 加 case              -> 不需要,BuildAddress 在描述符里
///
/// 唯一必须实现的差异逻辑就是 BuildAddress(各协议地址规则不同)。
/// </summary>
public interface IProtocolDescriptor
{
    /// <summary>协议键,与 settings.json 列表名对应,如 "ModbusTCP"。</summary>
    string Key { get; }

    /// <summary>显示名,如 "Modbus TCP"。</summary>
    string DisplayName { get; }

    /// <summary>该协议需要哪种连接方式。</summary>
    ConnectionKind ConnectionKind { get; }

    /// <summary>协议特有参数定义。配置界面按此自动生成输入框。</summary>
    IReadOnlyList<OptionDefinition> Options { get; }

    /// <summary>
    /// 合成完整地址。这是各协议唯一真正不同的逻辑。
    /// </summary>
    /// <param name="groupStart">组起始地址,如 "D100" / "1.400001" / "DB1.0"</param>
    /// <param name="varOffset">组内偏移,如 "10"</param>
    /// <param name="device">设备配置(需要读 Options,如 IsShortAddress)</param>
    /// <returns>完整地址;无法合成时返回空串</returns>
    string BuildAddress(string groupStart, string varOffset, DeviceConfig device);

    /// <summary>
    /// 地址合法性校验。配置界面实时提示用。
    /// </summary>
    /// <returns>null = 合法;非 null = 错误说明</returns>
    string? ValidateAddress(string fullAddress, DeviceConfig device);

    /// <summary>
    /// 校验协议参数是否齐全合法。导入时调用。
    /// </returns>
    string? ValidateOptions(DeviceConfig device);
}
