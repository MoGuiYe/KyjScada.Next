namespace KyjScada.Domain.Users;

/// <summary>
/// 权限位。老系统 8 位 + 新增 2 位(点动/强制写),共 10 位。
///
/// 为什么用位掩码而不是一堆 bool 字段:
///  1. 一个整数就能整体存库、整体判断,不用 10 列;
///  2. 加权限只加一个常量,不改用户模型和表结构;
///  3. 现场"整套权限"组合可以直接按位组合。
/// </summary>
[Flags]
public enum PermissionBit
{
    None = 0,

    /// <summary>1. 查看(只读浏览)。</summary>
    CanView = 1 << 0,

    /// <summary>2. 参数设置(写设定值)。</summary>
    CanWrite = 1 << 1,

    /// <summary>3. 报警确认。</summary>
    CanAckAlarm = 1 << 2,

    /// <summary>4. 历史查询/趋势。</summary>
    CanQueryHistory = 1 << 3,

    /// <summary>5. 报表查看与导出。</summary>
    CanViewReport = 1 << 4,

    /// <summary>6. 配置修改(设备/组/变量)。</summary>
    CanEditConfig = 1 << 5,

    /// <summary>7. 用户管理。</summary>
    CanManageUsers = 1 << 6,

    /// <summary>8. 系统设置(归档周期/保留期等)。</summary>
    CanEditSystem = 1 << 7,

    /// <summary>9. **点动**(按住运行、松开停止)。新增位。</summary>
    CanJog = 1 << 8,

    /// <summary>10. **强制写**(绕过逻辑强制置位)。新增位,危险,只给管理员。</summary>
    CanForce = 1 << 9,

    /// <summary>全部权限(仅内置管理员)。</summary>
    All = CanView | CanWrite | CanAckAlarm | CanQueryHistory | CanViewReport
        | CanEditConfig | CanManageUsers | CanEditSystem | CanJog | CanForce
}

/// <summary>权限位的中文说明(界面与提示用)。</summary>
public static class PermissionNames
{
    public static string Of(PermissionBit bit)
    {
        return bit switch
        {
            PermissionBit.CanView => "查看",
            PermissionBit.CanWrite => "参数设置",
            PermissionBit.CanAckAlarm => "报警确认",
            PermissionBit.CanQueryHistory => "历史查询",
            PermissionBit.CanViewReport => "报表查看",
            PermissionBit.CanEditConfig => "配置修改",
            PermissionBit.CanManageUsers => "用户管理",
            PermissionBit.CanEditSystem => "系统设置",
            PermissionBit.CanJog => "点动",
            PermissionBit.CanForce => "强制写",
            _ => "未知权限"
        };
    }

    /// <summary>按位展开成可读文本。</summary>
    public static string Describe(PermissionBit permissions)
    {
        if (permissions == PermissionBit.None) return "无权限";

        var parts = new List<string>();

        foreach (var bit in Enum.GetValues<PermissionBit>())
        {
            if (bit == PermissionBit.None || bit == PermissionBit.All) continue;

            if ((permissions & bit) == bit) parts.Add(Of(bit));
        }

        return parts.Count == 0 ? "无权限" : string.Join("、", parts);
    }
}