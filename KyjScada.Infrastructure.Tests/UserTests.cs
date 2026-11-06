using KyjScada.Domain.Users;
using KyjScada.Infrastructure.Users;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 用户与权限测试。
///
/// 两个重点:
///  1. **密码绝不明文落库**,且同密码不同盐得到不同哈希;
///  2. **权限位判定准确** —— 少一位就可能让操作工拿到强制写权限。
/// </summary>
public static class UserTests
{
    public static void RunAll(Action<string, Action> run)
    {
        RunHasher(run);
        RunPermissions(run);
        RunRepository(run);
    }

    private static void RunHasher(Action<string, Action> run)
    {
        run("密码哈希:同密码同盐结果一致", () =>
        {
            var salt = PasswordHasher.NewSalt();

            var h1 = PasswordHasher.Hash("abc123", salt);
            var h2 = PasswordHasher.Hash("abc123", salt);

            TestHelpers.AssertStr(h1, h2, "同盐应得到相同哈希");
        });

        run("密码哈希:同密码不同盐结果不同", () =>
        {
            var s1 = PasswordHasher.NewSalt();
            var s2 = PasswordHasher.NewSalt();

            TestHelpers.AssertTrue(s1 != s2, "两次盐应不同");

            var h1 = PasswordHasher.Hash("abc123", s1);
            var h2 = PasswordHasher.Hash("abc123", s2);

            TestHelpers.AssertTrue(h1 != h2, "不同盐应得到不同哈希");
        });

        run("密码哈希:哈希不等于明文", () =>
        {
            var salt = PasswordHasher.NewSalt();
            var hash = PasswordHasher.Hash("admin123", salt);

            TestHelpers.AssertTrue(hash != "admin123", "绝不能等于明文");
            TestHelpers.AssertTrue(hash.Length >= 32, "哈希长度应足够");
        });

        run("密码哈希:校验正确密码通过、错误密码失败", () =>
        {
            var salt = PasswordHasher.NewSalt();
            var hash = PasswordHasher.Hash("正确密码", salt);

            TestHelpers.AssertTrue(PasswordHasher.Verify("正确密码", salt, hash), "正确密码应通过");
            TestHelpers.AssertTrue(!PasswordHasher.Verify("错误密码", salt, hash), "错误密码应失败");
            TestHelpers.AssertTrue(!PasswordHasher.Verify("", salt, hash), "空密码应失败");
        });

        run("密码哈希:无盐直接抛异常(不允许无盐散列)", () =>
        {
            try
            {
                PasswordHasher.Hash("x", "");
                throw new Exception("应抛异常");
            }
            catch (ArgumentException)
            {
                // 期望
            }
        });

        run("密码哈希:参数非法时校验返回 false 不抛异常", () =>
        {
            TestHelpers.AssertTrue(!PasswordHasher.Verify("x", "不是十六进制", "zz"), "应返回 false");
            TestHelpers.AssertTrue(!PasswordHasher.Verify("x", "", ""), "空盐应返回 false");
        });
    }

    private static void RunPermissions(Action<string, Action> run)
    {
        run("权限位:Has 判定准确", () =>
        {
            var p = PermissionBit.CanView | PermissionBit.CanWrite;

            TestHelpers.AssertTrue((p & PermissionBit.CanView) == PermissionBit.CanView, "应有查看");
            TestHelpers.AssertTrue((p & PermissionBit.CanWrite) == PermissionBit.CanWrite, "应有写");
            TestHelpers.AssertTrue((p & PermissionBit.CanForce) != PermissionBit.CanForce, "不应有强制写");
            TestHelpers.AssertTrue((p & PermissionBit.CanJog) != PermissionBit.CanJog, "不应有点动");
        });

        run("权限位:共 10 个业务权限,含点动与强制写", () =>
        {
            var bits = Enum.GetValues<PermissionBit>()
                .Where(b => b != PermissionBit.None && b != PermissionBit.All)
                .ToList();

            TestHelpers.AssertEq(10, bits.Count, "应有 10 个业务权限位");

            TestHelpers.AssertTrue(bits.Contains(PermissionBit.CanJog), "应包含点动");
            TestHelpers.AssertTrue(bits.Contains(PermissionBit.CanForce), "应包含强制写");
        });

        run("权限位:All 包含全部 10 位", () =>
        {
            foreach (var b in Enum.GetValues<PermissionBit>())
            {
                if (b == PermissionBit.None || b == PermissionBit.All) continue;

                TestHelpers.AssertTrue((PermissionBit.All & b) == b, "All 应包含 " + b);
            }
        });

        run("权限位:中文描述", () =>
        {
            TestHelpers.AssertStr("无权限", PermissionNames.Describe(PermissionBit.None), "空权限");

            var text = PermissionNames.Describe(PermissionBit.CanView | PermissionBit.CanWrite);

            TestHelpers.AssertTrue(text.Contains("查看") && text.Contains("参数设置"),
                "描述应含查看与参数设置,实际 " + text);
        });

        run("权限位:账号 Has 与 Permissions 一致", () =>
        {
            var u = MakeAccount(PermissionBit.CanView | PermissionBit.CanAckAlarm);

            TestHelpers.AssertTrue(u.Has(PermissionBit.CanView), "应有查看");
            TestHelpers.AssertTrue(u.Has(PermissionBit.CanAckAlarm), "应能确认报警");
            TestHelpers.AssertTrue(!u.Has(PermissionBit.CanWrite), "不应能写");
        });
    }

    private static UserAccount MakeAccount(PermissionBit p)
    {
        return new UserAccount(1, "u", "h", "s", 1000, p, true, DateTime.Now, null);
    }

    private static void RunRepository(Action<string, Action> run)
    {
        run("用户库:首次种管理员,再种不重复", () =>
        {
            var repo = NewRepo("seed");
            var t = new DateTime(2026, 11, 5, 9, 0, 0);

            TestHelpers.AssertTrue(repo.SeedDefaultAdmin("admin123", t), "首次应创建");
            TestHelpers.AssertTrue(!repo.SeedDefaultAdmin("admin123", t), "第二次不应创建");

            TestHelpers.AssertEq(1, repo.GetAll().Count, "应只有 1 个用户");
        });

        run("用户库:密码不明文落库", () =>
        {
            var repo = NewRepo("plain");
            var t = new DateTime(2026, 11, 5, 9, 0, 0);

            repo.Create("zhang", "mypassword", PermissionBit.CanView, t);

            var u = repo.Find("zhang")!;

            TestHelpers.AssertTrue(u.PasswordHash != "mypassword", "不能存明文");
            TestHelpers.AssertTrue(u.Salt.Length > 0, "必须有盐");
            TestHelpers.AssertTrue(u.Iterations >= 10000, "迭代次数应足够");
        });

        run("用户库:登录成功/密码错/用户不存在", () =>
        {
            var repo = NewRepo("login");
            var t = new DateTime(2026, 11, 5, 9, 0, 0);

            repo.Create("li", "pass123", PermissionBit.CanView, t);

            TestHelpers.AssertTrue(repo.Authenticate("li", "pass123", t) is not null, "正确密码应登录成功");
            TestHelpers.AssertTrue(repo.Authenticate("li", "wrong", t) is null, "错误密码应失败");
            TestHelpers.AssertTrue(repo.Authenticate("nobody", "pass123", t) is null, "不存在应失败");
        });

        run("用户库:停用账号不能登录", () =>
        {
            var repo = NewRepo("inactive");
            var t = new DateTime(2026, 11, 5, 9, 0, 0);

            repo.Create("wang", "pass123", PermissionBit.CanView, t, isActive: false);

            TestHelpers.AssertTrue(repo.Authenticate("wang", "pass123", t) is null, "停用账号应拒绝登录");
        });

        run("用户库:登录成功记录登录时间", () =>
        {
            var repo = NewRepo("lastlogin");
            var t = new DateTime(2026, 11, 5, 9, 0, 0);

            repo.Create("zhao", "pass123", PermissionBit.CanView, t);

            var logged = repo.Authenticate("zhao", "pass123", t.AddHours(1));

            TestHelpers.AssertTrue(logged!.LastLoginAt is not null, "应记录登录时间");
            TestHelpers.AssertTrue(repo.Find("zhao")!.LastLoginAt is not null, "库里也应更新");
        });

        run("用户库:用户名重复拒绝创建", () =>
        {
            var repo = NewRepo("dup");
            var t = new DateTime(2026, 11, 5, 9, 0, 0);

            TestHelpers.AssertTrue(repo.Create("sun", "a", PermissionBit.CanView, t) > 0, "首次应成功");
            TestHelpers.AssertEq(-1, repo.Create("sun", "b", PermissionBit.CanView, t), "重复应返回 -1");
        });

        run("用户库:改权限生效", () =>
        {
            var repo = NewRepo("perm");
            var t = new DateTime(2026, 11, 5, 9, 0, 0);

            repo.Create("qian", "a", PermissionBit.CanView, t);

            TestHelpers.AssertTrue(
                repo.UpdatePermissions("qian", PermissionBit.CanView | PermissionBit.CanWrite, true),
                "应更新成功");

            var u = repo.Find("qian")!;

            TestHelpers.AssertTrue(u.Has(PermissionBit.CanWrite), "应获得写权限");
        });

        run("用户库:改密码后旧密码失效新密码可用", () =>
        {
            var repo = NewRepo("chpw");
            var t = new DateTime(2026, 11, 5, 9, 0, 0);

            repo.Create("zhou", "old123", PermissionBit.CanView, t);

            var oldSalt = repo.Find("zhou")!.Salt;

            TestHelpers.AssertTrue(repo.ChangePassword("zhou", "new456"), "改密应成功");

            TestHelpers.AssertTrue(repo.Authenticate("zhou", "old123", t) is null, "旧密码应失效");
            TestHelpers.AssertTrue(repo.Authenticate("zhou", "new456", t) is not null, "新密码应可用");

            TestHelpers.AssertTrue(repo.Find("zhou")!.Salt != oldSalt, "改密应换盐");
        });

        run("用户库:删除用户后查不到", () =>
        {
            var repo = NewRepo("del");
            var t = new DateTime(2026, 11, 5, 9, 0, 0);

            repo.Create("wu", "a", PermissionBit.CanView, t);

            TestHelpers.AssertTrue(repo.Delete("wu"), "应删除成功");
            TestHelpers.AssertTrue(repo.Find("wu") is null, "删除后应查不到");
        });

        run("用户库:用户名大小写不敏感", () =>
        {
            var repo = NewRepo("case");
            var t = new DateTime(2026, 11, 5, 9, 0, 0);

            repo.Create("Admin", "a", PermissionBit.CanView, t);

            TestHelpers.AssertTrue(repo.Find("admin") is not null, "小写应能查到");
            TestHelpers.AssertTrue(repo.Authenticate("ADMIN", "a", t) is not null, "大写应能登录");
        });
    }

    private static UserRepository NewRepo(string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "_work");

        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "users_" + name + ".db");

        if (File.Exists(path)) File.Delete(path);

        var repo = new UserRepository(path);
        repo.EnsureSchema();

        return repo;
    }
}