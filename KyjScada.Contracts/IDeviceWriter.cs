namespace KyjScada.Contracts;

/// <summary>
/// 设备写入抽象。写入闸门(Runtime)通过它下发,具体协议由实现层负责。
///
/// 只有闸门可以调用它 —— 任何绕过闸门的写操作都会失去权限校验和审计,
/// 而工业现场"误写一次设定值"可能直接造成事故。
/// </summary>
public interface IDeviceWriter
{
    /// <summary>
    /// 按变量名写入一个工程值。成功返回 true;失败通过 error 说明原因。
    /// </summary>
    bool WriteVariable(string varName, double value, out string error);
}