using KyjScada.Domain.Config;
using KyjScada.Infrastructure.Config;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 配置校验器测试。
/// 这些规则对应"配置错了不会报错、只会静默读错值"的真实坑。
/// </summary>
public static class ConfigValidatorTests
{
    public static void RunAll(Action<string, Action> run)
    {
        RunBasics(run);
        RunVariableRules(run);
        RunLayout(run);
        RunRealConfig(run);
    }

    private static ProjectConfig Project(params DeviceConfig[] devices)
    {
        var p = new ProjectConfig { ProjectName = "测试项目" };

        p.DevicesByProtocol["ModbusTCP"] = devices.ToList();

        return p;
    }

    private static DeviceConfig Device(string name, params GroupConfig[] groups)
    {
        var d = new DeviceConfig
        {
            DeviceName = name,
            ProtocolKey = "ModbusTCP",
            Connection = new ConnectionConfig { Kind = ConnectionKind.Tcp, IpAddress = "127.0.0.1", Port = 502 }
        };

        foreach (var g in groups) d.GroupList.Add(g);

        return d;
    }

    private static GroupConfig Group(string name, int length, params VariableConfig[] vars)
    {
        var g = new GroupConfig { GroupName = name, Start = "1.400001", Length = length, IsActive = true };

        foreach (var v in vars) g.VarList.Add(v);

        return g;
    }

    private static VariableConfig Var(string name, string start = "1.40001", string type = "Float")
    {
        return new VariableConfig
        {
            VarName = name,
            VarType = type,
            Start = start,
            VarAddress = start,
            Scale = 1f
        };
    }

    private static void RunBasics(Action<string, Action> run)
    {
        run("配置校验:正常配置无问题", () =>
        {
            var p = Project(Device("D1", Group("G1", 10, Var("V1"), Var("V2", "1.40003"))));

            var problems = ConfigValidator.Validate(p);

            TestHelpers.AssertEq(0, problems.Count,
                "应无问题,实际: " + string.Join(" | ", problems));
        });

        run("配置校验:项目名为空", () =>
        {
            var p = Project(Device("D1", Group("G1", 10, Var("V1"))));
            p.ProjectName = "";

            var problems = ConfigValidator.Validate(p);

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("项目名称")), "应报项目名为空");
        });

        run("配置校验:没有设备", () =>
        {
            var p = new ProjectConfig { ProjectName = "P" };

            var problems = ConfigValidator.Validate(p);

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("没有任何设备")), "应报无设备");
        });

        run("配置校验:设备重名", () =>
        {
            var p = Project(
                Device("同样的名字", Group("G1", 10, Var("V1"))),
                Device("同样的名字", Group("G2", 10, Var("V2")))
            );

            var problems = ConfigValidator.Validate(p);

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("设备名称重复")), "应报设备重名");
        });

        run("配置校验:设备没有通讯组", () =>
        {
            var p = Project(Device("D1"));

            var problems = ConfigValidator.Validate(p);

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("没有配置任何通讯组")), "应报无组");
        });
    }

    private static void RunVariableRules(Action<string, Action> run)
    {
        run("配置校验:Scale 为 0(读数恒为 0 的头号原因)", () =>
        {
            var v = Var("V1");
            v.Scale = 0f;

            var problems = ConfigValidator.Validate(Project(Device("D1", Group("G1", 10, v))));

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("Scale 为 0")),
                "应报 Scale=0,实际: " + string.Join(" | ", problems));
        });

        run("配置校验:变量名重复(同设备内)", () =>
        {
            var problems = ConfigValidator.Validate(Project(
                Device("D1", Group("G1", 10, Var("同名"), Var("同名", "1.40003")))));

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("变量名重复")), "应报变量重名");
        });

        run("配置校验:变量名为空", () =>
        {
            var problems = ConfigValidator.Validate(Project(
                Device("D1", Group("G1", 10, Var("")))));

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("变量名为空")), "应报变量名为空");
        });

        run("配置校验:未知数据类型", () =>
        {
            var problems = ConfigValidator.Validate(Project(
                Device("D1", Group("G1", 10, Var("V1", "1.40001", "Flout")))));

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("未知数据类型")), "应报未知类型");
        });

        run("配置校验:启用上限但阈值为 0", () =>
        {
            var v = Var("V1");
            v.HighAlarmEnable = true;
            v.HighAlarmValue = 0f;

            var problems = ConfigValidator.Validate(Project(Device("D1", Group("G1", 10, v))));

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("上限报警但阈值无效")),
                "应报上限阈值无效,实际: " + string.Join(" | ", problems));
        });

        run("配置校验:上限小于下限(设置反了)", () =>
        {
            var v = Var("V1");
            v.HighAlarmEnable = true;
            v.HighAlarmValue = 10f;
            v.LowAlarmEnable = true;
            v.LowAlarmValue = 50f;

            var problems = ConfigValidator.Validate(Project(Device("D1", Group("G1", 10, v))));

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("小于下限")),
                "应报上下限反了,实际: " + string.Join(" | ", problems));
        });

        run("配置校验:死区为负", () =>
        {
            var v = Var("V1");
            v.AlarmDeadband = -1f;

            var problems = ConfigValidator.Validate(Project(Device("D1", Group("G1", 10, v))));

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("死区不能为负")), "应报死区为负");
        });

        run("配置校验:组内没有变量", () =>
        {
            var problems = ConfigValidator.Validate(Project(Device("D1", Group("空组", 10))));

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("组内没有变量")), "应报空组");
        });

        run("配置校验:已知类型全部通过", () =>
        {
            foreach (var t in ConfigValidator.KnownTypes)
            {
                TestHelpers.AssertTrue(ConfigValidator.IsKnownType(t), "应识别类型 " + t);
                TestHelpers.AssertTrue(ConfigValidator.IsKnownType(t.ToLowerInvariant()), "应忽略大小写 " + t);
            }

            TestHelpers.AssertTrue(!ConfigValidator.IsKnownType("Flout"), "拼错应不通过");
            TestHelpers.AssertTrue(!ConfigValidator.IsKnownType(""), "空应不通过");
        });
    }

    private static void RunLayout(Action<string, Action> run)
    {
        run("配置校验:变量超出组长度(委托采集层规则)", () =>
        {
            // Float 占 2 寄存器;组长度 10,变量偏移 0 没问题;
            // 再放一个偏移 9 的 Float → 9+2=11 > 10 越界
            var problems = ConfigValidator.Validate(Project(
                Device("D1", Group("G1", 10, Var("V1"), Var("V2", "1.40010")))));

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("超出组长")),
                "应报越界,实际: " + string.Join(" | ", problems));
        });

        run("配置校验:组长度无效", () =>
        {
            var problems = ConfigValidator.Validate(Project(
                Device("D1", Group("G1", 0, Var("V1")))));

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("长度无效")), "应报长度无效");
        });
    }

    private static void RunRealConfig(Action<string, Action> run)
    {
        run("配置校验:现场配置可校验并输出问题数", () =>
        {
            var path = FindRealSettings();
            if (path is null) return;

            var projects = SettingsJsonCodec.LoadFile(path);
            var project = SettingsJsonCodec.ActiveProject(projects);

            var problems = ConfigValidator.Validate(project);

            Console.WriteLine("        现场配置问题数: " + problems.Count);

            foreach (var p in problems.Take(8)) Console.WriteLine("          - " + p);

            // 现场配置允许存在"报警阈值可疑"这类问题,但不应有结构性错误
            var structural = problems.Count(x =>
                x.Contains("无法解析") || x.Contains("超出组长") || x.Contains("Scale 为 0")
                || x.Contains("未知数据类型") || x.Contains("没有配置任何通讯组"));

            TestHelpers.AssertEq(0, structural, "现场配置不应有结构性错误");
        });
    }

    private static string? FindRealSettings()
    {
        var baseDir = AppContext.BaseDirectory;

        var probe = Path.Combine(baseDir, "_work");
        if (Directory.Exists(probe))
        {
            var hit = Directory.GetFiles(probe, "ModbusTCP.json");
            if (hit.Length > 0) return hit[0];
        }

        var appDir = Path.Combine(baseDir, "..", "..", "..", "..", "KyjScada.App", "bin", "Debug", "net8.0-windows", "settings");
        if (Directory.Exists(appDir))
        {
            var hit2 = Directory.GetFiles(appDir, "ModbusTCP.json");
            if (hit2.Length > 0) return hit2[0];
        }

        return null;
    }
}