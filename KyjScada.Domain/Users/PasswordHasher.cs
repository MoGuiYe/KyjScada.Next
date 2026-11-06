using System.Security.Cryptography;

namespace KyjScada.Domain.Users;

/// <summary>
/// 密码哈希。**PBKDF2-SHA256 + 每用户独立盐 + 迭代次数**。
///
/// 为什么不能像老系统那样存明文或简单散列:
///  现场账号往往就是管理员常用密码,一旦配置/数据库被拷走,
///  明文等于把设备控制权直接交出去;而单次散列(MD5/SHA1)在字典面前几乎无效。
///
/// 参数选择:100000 次迭代在工控机上约 30~60ms ——
/// 登录不觉得慢,暴力破解成本却抬高了几个数量级。
/// </summary>
public static class PasswordHasher
{
    public const int Iterations = 100_000;
    public const int SaltBytes = 16;
    public const int HashBytes = 32;

    /// <summary>生成新盐(十六进制)。</summary>
    public static string NewSalt()
    {
        var salt = new byte[SaltBytes];

        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(salt);
        }

        return Convert.ToHexString(salt);
    }

    /// <summary>计算哈希(十六进制)。saltHex 为 null/空 时抛异常 —— 不允许无盐散列。</summary>
    public static string Hash(string password, string saltHex, int iterations = Iterations)
    {
        if (string.IsNullOrEmpty(saltHex))
        {
            throw new ArgumentException("必须提供盐", nameof(saltHex));
        }

        if (iterations <= 0) iterations = Iterations;

        var salt = Convert.FromHexString(saltHex);

        using var pbkdf2 = new Rfc2898DeriveBytes(
            password ?? "", salt, iterations, HashAlgorithmName.SHA256);

        return Convert.ToHexString(pbkdf2.GetBytes(HashBytes));
    }

    /// <summary>
    /// 校验密码。**用固定时间比较**(CryptographicOperations.FixedTimeEquals),
    /// 避免按字节提前返回泄露"前几位对了"这种信息。
    /// </summary>
    public static bool Verify(string password, string saltHex, string expectedHashHex, int iterations = Iterations)
    {
        if (string.IsNullOrEmpty(saltHex) || string.IsNullOrEmpty(expectedHashHex)) return false;

        try
        {
            var actual = Hash(password, saltHex, iterations);

            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual),
                Convert.FromHexString(expectedHashHex));
        }
        catch
        {
            return false;
        }
    }
}