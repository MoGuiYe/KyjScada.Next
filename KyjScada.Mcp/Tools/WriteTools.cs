using System.ComponentModel;
using ModelContextProtocol.Server;

namespace KyjScada.Mcp.Tools;

/// <summary>
/// MCP 写工具。铁律:
///  1. 本进程不直接碰 PLC/串口 —— 一切写入经由主程序注入的 WriteHandler
///     (内部走 CommandGate:权限校验 + 最小间隔限速 + 审计),避免双进程抢通道
///  2. 主程序未注册回调(未登录/未启用写入)时,工具必须诚实拒绝,绝不假装成功
///  3. 方法名自动转 snake_case(write_variable)
/// </summary>
[McpServerToolType]
public sealed class WriteTools
{
    /// <summary>
    /// 主程序启动后注入:(变量名, 值) -> 结果描述。
    /// 返回以 "OK" 开头表示成功,其余为拒绝/失败原因。
    /// </summary>
    public static Func<string, double, string>? WriteHandler { get; set; }

    [McpServerTool, Description("向指定变量写入值(仅可写变量;受用户权限与写入限速约束)。返回写入结果。")]
    public string WriteVariable(
        [Description("变量名,例如 KYJ1_SetPress")] string name,
        [Description("要写入的数值")] double value)
    {
        var handler = WriteHandler;
        if (handler == null)
            return "拒绝:写通道未启用(主程序未运行、当前用户无写权限或写闸门未开启)。";

        if (string.IsNullOrWhiteSpace(name))
            return "拒绝:变量名不能为空。";
        if (double.IsNaN(value) || double.IsInfinity(value))
            return "拒绝:非法数值。";

        try
        {
            return handler(name.Trim(), value);
        }
        catch (Exception ex)
        {
            // 写通道抛异常时**不能**让它变成协议层的未知错误:
            // 究竟下发没下发说不清,AI 客户端可能据此重试 —— 对 PLC 来说就是重复写。
            // 明确告诉它"结果未知,请先读回/人工核实",不要重试。
            return "写入结果未知(写通道异常): " + ex.Message
                + "\n请勿直接重试 —— 先用 read_variable 读回该点确认设备是否已接受本次写入。";
        }
    }

    /// <summary>主程序调用:探测写通道是否可用(用于 get_write_status)。</summary>
    public static bool IsWriteEnabled => WriteHandler != null;

    [McpServerTool, Description("查询写通道当前状态(是否启用、说明)。不执行任何写入。")]
    public string GetWriteStatus()
    {
        return WriteHandler != null
            ? "写通道:已启用(经由主程序 CommandGate,受权限与最小写入间隔约束)。"
            : "写通道:未启用。原因:主程序未注入写回调(未登录、用户无 CanWrite 权限,或写使能未打开)。";
    }
}
