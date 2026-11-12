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

    /// <summary>
    /// 建表 / 切 WAL 的串行锁。journal_mode 是库级持久设置,切换要抢独占锁;
    /// 登录(界面线程)与 MCP 查询可能同时首次打开本库,不加锁就会撞锁失败。
    /// </summary>
    private static readonly object SchemaLock = new ();

    /// <summary>本库文件的建表状态,跨实例共享。</summary>
    private static readonly HashSet<string> SchemaReady = new (StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// WAL 切换的失败次数与下次重试时刻(按库文件)。
    ///
    /// WAL 与"建表"必须分开记账:
    ///   · 表建好了就是建好了,不需要因为 WAL 没切成就每次重做;
    ///   · WAL 没切成功要**有限地**重试,不能无限重试 ——
    ///     每次 PRAGMA journal_mode 都要抢库级独占锁,配合 busy_timeout=5000
    ///     每次可能阻塞数秒。
    /// 所以:失败后按指数退避重试,试满 MaxWalAttempts 次就放弃(只留 LastWalError)。
    /// </summary>
    private static readonly Dictionary<string, (int Attempts, DateTime NextTryUtc)> WalRetry
        = new (StringComparer.OrdinalIgnoreCase);

    private const int MaxWalAttempts = 5;

    /// <summary>
    /// WAL 未能启用的原因,**按库文件**记账(诊断用)。
    ///
    /// 早先是单个静态属性:多库场景下(本轮测试大量临时库、生产换配置目录也是新库)
    /// 库 B 切 WAL 成功就会把它清成 null,把库 A 的失败记录**一并抹掉** ——
    /// 现场"哪个库卡在 delete 模式"再也看不出来,而 B 那次清理本身毫无必要。
    /// 现在与 <see cref="WalRetry"/> 同一口径:只清/只记本库的。
    /// </summary>
    private static readonly Dictionary<string, string> WalErrors
        = new (StringComparer.OrdinalIgnoreCase);

    /// <summary>该库最近一次 WAL 失败的原因;该库切成功或从未尝试过则为 null。</summary>
    public static string? GetLastWalError (string dbPath)
    {
        lock (SchemaLock)
        {
            return WalErrors.TryGetValue (dbPath ?? "", out var e) ? e : null;
        }
    }

    /// <summary>
    /// 该库是否还有"待重试的 WAL 切换"。
    ///
    /// 表建好之后 EnsureSchema 本来可以直接返回,但只要 WAL 还没切成功、
    /// 且没到重试上限,就得再进来一次 —— 否则 TryEnableWal 的退避形同虚设,
    /// 实际只尝试一次,失败就**永久**停在 delete 模式(登录写 LastLoginAt 与
    /// MCP 查询并发时会报 SQLITE_BUSY)。
    /// </summary>
    private static bool WalPending(string dbPath)
    {
        lock (SchemaLock)
        {
            // 退避期没到不算"待办":否则每次 EnsureSchema 都白开一个连接,
            // 进去 TryEnableWal 一看时间不到又直接 return。
            return WalRetry.TryGetValue(dbPath, out var state)
                   && state.Attempts < MaxWalAttempts
                   && DateTime.UtcNow >= state.NextTryUtc;
        }
    }

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

            // 不用 Shared 缓存:SQLite 官方已列为不推荐,与 WAL 组合时并发写会
            // 返回 SQLITE_LOCKED(历史库那边实测过)。并发靠 WAL + busy_timeout。
            Cache = SqliteCacheMode.Private
        };

        var conn = new SqliteConnection(builder.ToString());
        conn.Open();

        // journal_mode 是库级独占操作,不在这里做(每次开连接都做会让并发互相阻塞);
        // 只在 EnsureSchema 里做,切不成按退避重试。
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=normal;";
        cmd.ExecuteNonQuery();

        return conn;
    }

    /// <summary>建表(幂等)。表建好之后不再重复;WAL 单独按退避重试。</summary>
    public void EnsureSchema()
    {
        lock (SchemaLock)
        {
            var schemaDone = SchemaReady.Contains(_dbPath);

            // 表已建好但 WAL 还没切成功:必须再进来一次去重试 WAL。
            // 早先这里一律 return,于是 TryEnableWal 的指数退避与 MaxWalAttempts
            // 永远不会被触发 —— 实际上只尝试一次,首次切失败就永久停在 delete 模式。
            if (schemaDone && !WalPending(_dbPath)) return;

            using var conn = OpenConnection();

            TryEnableWal(conn);

            if (schemaDone) return;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = CreateSql;
                cmd.ExecuteNonQuery();
            }

            // 表已经建好了,这一步不需要因为 WAL 没切成就重做
            SchemaReady.Add(_dbPath);
        }
    }

    /// <summary>
    /// 尝试把库切到 WAL,失败则按指数退避安排下次重试。
    ///
    /// **必须看返回值**:被别的连接占着时这句会"成功但没切过去",返回的仍是 delete。
    /// 切不过去不影响可用性(库仍是 delete 模式),所以不抛 ——
    /// 但**必须记下来并安排重试**,否则这个库在本进程里就永远停在 delete 模式了。
    /// </summary>
    private void TryEnableWal(SqliteConnection conn)
    {
        int attempts;

        lock (SchemaLock)
        {
            if (WalRetry.TryGetValue(_dbPath, out var state))
            {
                if (state.Attempts >= MaxWalAttempts) return;

                if (DateTime.UtcNow < state.NextTryUtc) return;

                attempts = state.Attempts;
            }
            else
            {
                attempts = 0;
            }
        }

        string? mode = null;
        string? error = null;

        try
        {
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=wal;";

            // 返回值就是切换后的实际模式:不等于 "wal" 即没切成功
            mode = pragma.ExecuteScalar() as string;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        lock (SchemaLock)
        {
            if (string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                WalRetry.Remove(_dbPath);

                // 只清**本库**的失败记录:别的库还卡在 delete 模式时,
                // 本库切成功不该把它的记录一并抹掉。
                WalErrors.Remove(_dbPath);

                return;
            }

            var next = attempts + 1;

            // 指数退避,封顶 60 秒
            var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, next)));

            WalRetry[_dbPath] = (next, DateTime.UtcNow + delay);

            WalErrors[_dbPath] = "用户库未能启用 WAL(第 " + next + " 次,返回 " + (mode ?? "null") + ")"
                + (error is null ? "" : ":" + error)
                + (next >= MaxWalAttempts
                    ? " —— 已达重试上限,本进程不再尝试(并发写会报 SQLITE_BUSY)"
                    : ",将在 " + (int)delay.TotalSeconds + " 秒后重试");
        }
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
    /// 账号不存在时用来走一遍"同样代价"散列校验的假盐/假哈希。
    /// 目的只是让"用户不存在"和"密码错"耗时接近,防时序账号枚举。
    /// </summary>
    private static readonly string DummySalt = PasswordHasher.NewSalt();

    private static readonly string DummyHash = PasswordHasher.Hash("-", DummySalt);

    /// <summary>
    /// 登录校验。成功返回账号并记录登录时间;失败返回 null(不区分"用户不存在"和"密码错",
    /// 避免账号探测)。
    /// </summary>
    public UserAccount? Authenticate(string userName, string password, DateTime now)
    {
        var user = Find(userName);

        if (user is null)
        {
            // 账号不存在也要走一遍**同样代价**的散列校验:
            // 直接 return 比"密码错"快一个数量级,本机任意进程可以据此逐个试出
            // 哪些账号存在(再配合默认口令就是定向爆破)。
            PasswordHasher.Verify(password ?? "", DummySalt, DummyHash, PasswordHasher.Iterations);

            return null;
        }

        var ok = PasswordHasher.Verify(password, user.Salt, user.PasswordHash, user.Iterations);

        // 先校验密码再看 IsActive:否则"已停用"的账号会跳过散列,
        // 时间差一样能区分出"这个账号确实存在,只是被停用了"。
        if (!ok) return null;

        if (!user.IsActive) return null;

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