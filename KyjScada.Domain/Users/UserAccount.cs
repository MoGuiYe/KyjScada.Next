namespace KyjScada.Domain.Users;

/// <summary>用户账号。密码只以 PBKDF2 哈希 + 盐 的形式保存,绝不存明文。</summary>
public sealed record UserAccount(
    int Id,
    string UserName,
    string PasswordHash,
    string Salt,
    int Iterations,
    PermissionBit Permissions,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? LastLoginAt)
{
    /// <summary>是否具备某个权限位。</summary>
    public bool Has(PermissionBit bit) => (Permissions & bit) == bit;

    /// <summary>权限的中文描述(界面显示)。</summary>
    public string PermissionText => PermissionNames.Describe(Permissions);
}