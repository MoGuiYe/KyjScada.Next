using KyjScada.Domain.Users;
using Microsoft.Data.Sqlite;

namespace KyjScada.Infrastructure.Users;

/// <summary>
/// 用户仓储。SQLite,与历史/报警同目录不同文件(`data\users.db`)。
///
/// 密码策略集中在 PasswordHasher:PBKDF2-SHA256、每用户独立盐、10 万次迭代。
/// 这里只负责存取和校验,不碰散列算法细节。
/// </summary>
public sealed class UserRepository
{
    private readonly string _dbPath;
    private bool _schemaReady;

    public UserRepository(string dbPath)
    {
        _dbPath = dbPath;
    }

    private const string CreateSql = @"
CREATE TABLE IF NOT EXISTS Users (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    UserName    TEXT    NOT NULL UNIQUE,
    PasswordHash TEXT   NOT NULL,
    Salt        TEXT    NOT NULL,
    Iterations  INTEGER NOT NULL,
    Permissions INTEGER NOT NULL,
    IsActive    INTEGER NOT NULL,
    CreatedAt   TEXT    NOT NULL,
    LastLoginAt TEXT    NULL
);
";

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        var conn = new SqliteConnection(builder.ToString());
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=wal; PRAGMA synchronous=normal;";
        cmd.ExecuteNonQuery();

        return conn;
    }

    public void EnsureSchema()
    {
        if (_schemaReady) return;

        using var conn = OpenConnection();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = CreateSql;
            cmd.ExecuteNonQuery();
        }

        _schemaReady = true;
    }

    private static UserAccount Read(SqliteDataReader r)
    {
        DateTime? last = null;

        if (!r.IsDBNull(8))
        {
            if (DateTime.TryParse(r.GetString(8), out var t)) last = t;
        }

        return new UserAccount(
            r.GetInt32(0),
            r.GetString(1),
            r.GetString(2),
            r.GetString(3),
            r.GetInt32(4),
            (PermissionBit)r.GetInt32(5),
            r.GetInt32(6) != 0,
            DateTime.Parse(r.GetString(7)),
            last);
    }

    /// <summary>全部用户(按名称排序)。</summary>
    public List<UserAccount> GetAll()
    {
        EnsureSchema();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText =
            "SELECT Id, UserName, PasswordHash, Salt, Iterations, Permissions, IsActive, CreatedAt, LastLoginAt " +
            "FROM Users ORDER BY UserName;";

        var list = new List<UserAccount>();

        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(Read(reader));

        return list;
    }

    public UserAccount? Find(string userName)
    {
        EnsureSchema();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText =
            "SELECT Id, UserName, PasswordHash, Salt, Iterations, Permissions, IsActive, CreatedAt, LastLoginAt " +
            "FROM Users WHERE UserName = @n COLLATE NOCASE;";

        cmd.Parameters.AddWithValue("@n", userName ?? "");

        using var reader = cmd.ExecuteReader();

        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// 登录校验。成功返回账号并记录登录时间;失败返回 null(不区分"用户不存在"和"密码错",
    /// 避免账号探测)。
    /// </summary>
    public UserAccount? Authenticate(string userName, string password, DateTime now)
    {
        var user = Find(userName);

        if (user is null) return null;
        if (!user.IsActive) return null;

        if (!PasswordHasher.Verify(password, user.Salt, user.PasswordHash, user.Iterations))
        {
            return null;
        }

        EnsureSchema();

        using (var conn = OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE Users SET LastLoginAt = @t WHERE Id = @id;";
            cmd.Parameters.AddWithValue("@t", now.ToString("o"));
            cmd.Parameters.AddWithValue("@id", user.Id);
            cmd.ExecuteNonQuery();
        }

        return user with { LastLoginAt = now };
    }

    /// <summary>
    /// 新建用户。密码在这里就地散列,调用方永远拿不到明文。
    /// 返回新用户 Id;用户名重复返回 -1。
    /// </summary>
    public int Create(string userName, string password, PermissionBit permissions, DateTime now, bool isActive = true)
    {
        EnsureSchema();

        if (string.IsNullOrWhiteSpace(userName)) return -1;

        // 先 Trim 再查重、再入库。
        // 原来用原始输入查重、用 Trim 后的值插入:传进来 "admin "(末尾带空格)时
        // Find("admin ") 匹配不到已存在的 "admin"(SQLite 的 NOCASE 只折叠大小写、
        // 不去空格),随后 INSERT @n = "admin" 撞 UNIQUE 约束抛 SqliteException,
        // 界面上表现成"新建用户直接报错退出"。
        var name = userName.Trim();

        if (name.Length > 32) return -1;   // 超长名字会撑破用户列表列宽

        if (Find(name) is not null) return -1;

        if (!IsAcceptablePassword(password)) return -1;

        var salt = PasswordHasher.NewSalt();
        var hash = PasswordHasher.Hash(password, salt);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
INSERT INTO Users (UserName, PasswordHash, Salt, Iterations, Permissions, IsActive, CreatedAt, LastLoginAt)
VALUES (@n, @h, @s, @i, @p, @a, @c, NULL);
SELECT last_insert_rowid();";

        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@h", hash);
        cmd.Parameters.AddWithValue("@s", salt);
        cmd.Parameters.AddWithValue("@i", PasswordHasher.Iterations);
        cmd.Parameters.AddWithValue("@p", (int)permissions);
        cmd.Parameters.AddWithValue("@a", isActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@c", now.ToString("o"));

        var id = Convert.ToInt32(cmd.ExecuteScalar());

        return id;
    }

    /// <summary>更新权限/启用状态(不改密码)。</summary>
    public bool UpdatePermissions(string userName, PermissionBit permissions, bool isActive)
    {
        EnsureSchema();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "UPDATE Users SET Permissions = @p, IsActive = @a WHERE UserName = @n COLLATE NOCASE;";

        cmd.Parameters.AddWithValue("@p", (int)permissions);
        cmd.Parameters.AddWithValue("@a", isActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@n", userName ?? "");

        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 密码强度底线。原来**完全不校验**:空密码、单空格、1 位数字都能建号,
    /// 而密码是现场唯一的权限边界(操作员能下发写值)。这里只做最低限度的拦截,
    /// 更严的策略交给界面侧提示,避免把现场习惯一口气全堵死。
    /// </summary>
    public static bool IsAcceptablePassword (string? password)
    {
        if (string.IsNullOrWhiteSpace (password)) return false;

        if (password.Length < 6) return false;

        if (password.Length > 128) return false;   // PBKDF2 输入过长纯属浪费

        return password.Trim().Length > 0;
    }

    /// <summary>改密码:重新生成盐并写回哈希。</summary>
    public bool ChangePassword(string userName, string newPassword)
    {
        EnsureSchema();

        // 改密码同样要过强度检查,否则"新建时拦住、改密码时又能设空"等于没拦
        if (!IsAcceptablePassword (newPassword)) return false;

        var salt = PasswordHasher.NewSalt();
        var hash = PasswordHasher.Hash(newPassword, salt);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText =
            "UPDATE Users SET PasswordHash = @h, Salt = @s, Iterations = @i WHERE UserName = @n COLLATE NOCASE;";

        cmd.Parameters.AddWithValue("@h", hash);
        cmd.Parameters.AddWithValue("@s", salt);
        cmd.Parameters.AddWithValue("@i", PasswordHasher.Iterations);
        cmd.Parameters.AddWithValue("@n", userName ?? "");

        return cmd.ExecuteNonQuery() > 0;
    }

    public bool Delete(string userName)
    {
        EnsureSchema();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "DELETE FROM Users WHERE UserName = @n COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("@n", userName ?? "");

        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 首次运行时种一个管理员账号,否则系统没有任何人能登录。
    /// **默认密码必须现场立刻改掉** —— 界面上会提示。
    /// </summary>
    public bool SeedDefaultAdmin(string defaultPassword, DateTime now)
    {
        EnsureSchema();

        if (GetAll().Count > 0) return false;

        return Create("admin", defaultPassword, PermissionBit.All, now) > 0;
    }
}