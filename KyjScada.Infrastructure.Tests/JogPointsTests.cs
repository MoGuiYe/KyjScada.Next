using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Infrastructure.Acquisition;
using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 点动/急停候选集与批写测试(界面第 7 页「急停/点动」的安全逻辑)。
///
/// 为什么要测这一层而不是只截图:
///  "哪些点允许被点动/急停"判错 = 把命令写到测量值寄存器上,现场会出事故。
///  规则已从 WPF 代码抽到 <see cref="JogPoints"/>,因此可以脱离界面断言。
/// </summary>
public static class JogPointsTests
{
    public static void RunAll(Action<string, Action> run)
    {
        RunSelect(run);
        RunEStop(run);
        RunRealConfig(run);
    }

    private sealed class FakeWriter : IDeviceWriter
    {
        public readonly List<(string Name, double Value)> Sent = new();

        public bool WriteVariable(string varName, double value, out string error)
        {
            Sent.Add((varName, value));
            error = "";
            return true;
        }
    }

    private static GroupConfig Group(string name, string start, bool writable, params (string Name, string Type, string Start)[] vars)
    {
        var g = new GroupConfig { GroupName = name, Start = start, Length = (ushort)vars.Length, IsWritable = writable };

        foreach (var v in vars)
        {
            g.VarList.Add(new VariableConfig { VarName = v.Name, VarType = v.Type, Start = v.Start, Scale = 1f });
        }

        return g;
    }

    // ==================== 候选集规则 ====================

    private static void RunSelect(Action<string, Action> run)
    {
        run("点动候选:组名含「设定值」默认开放(老配置没有 IsWritable 字段)", () =>
        {
            var dev = new DeviceConfig { DeviceName = "D1" };

            dev.GroupList.Add(Group("设定值", "1.400101", false,
                ("SP1", "Float", "1.400101"),
                ("SP2", "Float", "1.400103")));

            var points = JogPoints.Select(dev, _ => true);

            TestHelpers.AssertEq(2, points.Count, "应列出 2 个候选");
            TestHelpers.AssertEq(0, points.Count(p => p.IsBool), "都不是 Bool");
        });

        run("点动候选:测量值组默认不开放(写进去会干扰设备)", () =>
        {
            var dev = new DeviceConfig { DeviceName = "D1" };

            dev.GroupList.Add(Group("实时组", "1.300001", false,
                ("T1", "Float", "1.300001"),
                ("T2", "Float", "1.300003")));

            var points = JogPoints.Select(dev, _ => true);

            TestHelpers.AssertEq(0, points.Count, "实时组不应有候选点");
        });

        run("点动候选:IsWritable 显式标记优先于组名(状态值组可被授权)", () =>
        {
            var dev = new DeviceConfig { DeviceName = "D1" };

            var g = Group("状态值", "1.000001", true,
                ("Run", "Bool", "1.000001"),
                ("Alarm", "Bool", "1.000002"));

            dev.GroupList.Add(g);

            var points = JogPoints.Select(dev, _ => true);

            TestHelpers.AssertEq(2, points.Count, "标记可写后应列出");
            TestHelpers.AssertEq(2, points.Count(p => p.IsBool), "两个都是 Bool");
        });

        run("点动候选:闸门判定为不可写的点被过滤(只读寄存器/位地址)", () =>
        {
            var dev = new DeviceConfig { DeviceName = "D1" };

            dev.GroupList.Add(Group("设定值", "1.400101", true,
                ("OK1", "Float", "1.400101"),
                ("BIT1", "Bool", "1.400103.01")));

            // 模拟闸门:只有 OK1 可写
            var points = JogPoints.Select(dev, name => name == "OK1");

            TestHelpers.AssertEq(1, points.Count, "只应剩 OK1");
            TestHelpers.AssertStr("OK1", points[0].VarName, "剩下的变量名");
        });
    }

    // ==================== 急停批写 ====================

    private static void RunEStop(Action<string, Action> run)
    {
        run("急停:目标只取 Bool 点,数值点不参与", () =>
        {
            var all = new List<JogPoint>
            {
                new("设定值", "SP1", "Float", ""),
                new("状态值", "Run", "Bool", ""),
                new("状态值", "Stop", "Bool", "")
            };

            var targets = JogPoints.EStopTargets(all);

            TestHelpers.AssertEq(2, targets.Count, "只应有 2 个 Bool");
            TestHelpers.AssertTrue(targets.All(t => t.IsBool), "目标必须全是 Bool");
        });

        run("急停:全部 Bool 点写 0,每次下发都留审计", () =>
        {
            var writer = new FakeWriter();
            var dev = new DeviceConfig { DeviceName = "D1" };

            var g = Group("状态值", "1.000001", true,
                ("Run", "Bool", "1.000001"),
                ("Fault", "Bool", "1.000002"),
                ("Remote", "Bool", "1.000003"));

            dev.GroupList.Add(g);

            var gate = new CommandGate
            {
                Enabled = true,
                Writer = writer,
                MinInterval = TimeSpan.Zero
            };

            gate.BuildWritables(dev, _ => true);

            var targets = JogPoints.EStopTargets(JogPoints.Select(dev, gate.IsWritable));

            var ok = 0;

            foreach (var t in targets)
            {
                if (gate.Write(t.VarName, 0, "estop").Success) ok++;
            }

            TestHelpers.AssertEq(3, targets.Count, "应有 3 个急停目标");
            TestHelpers.AssertEq(3, ok, "3 次写入都应成功");
            TestHelpers.AssertEq(3, writer.Sent.Count, "应下发 3 次");
            TestHelpers.AssertTrue(writer.Sent.All(s => s.Value == 0), "每次写的都必须是 0");
            TestHelpers.AssertEq(3, gate.Audit.Count, "审计应记录 3 条");
            TestHelpers.AssertTrue(gate.Audit.All(a => a.ModuleId == "estop"), "审计来源应为 estop");
        });

        run("急停:闸门关闭时一个字节都不下发", () =>
        {
            var writer = new FakeWriter();
            var dev = new DeviceConfig { DeviceName = "D1" };

            dev.GroupList.Add(Group("状态值", "1.000001", true, ("Run", "Bool", "1.000001")));

            var gate = new CommandGate
            {
                Enabled = false,
                Writer = writer,
                MinInterval = TimeSpan.Zero
            };

            gate.BuildWritables(dev, _ => true);

            var targets = JogPoints.EStopTargets(JogPoints.Select(dev, gate.IsWritable));
            var r = gate.Write(targets[0].VarName, 0, "estop");

            TestHelpers.AssertTrue(!r.Success, "闸门关闭应被拒");
            TestHelpers.AssertTrue(r.Verdict == WriteVerdict.Disabled, "原因应为未启用");
            TestHelpers.AssertEq(0, writer.Sent.Count, "不应下发");
        });
    }

    // ==================== 现场配置实测 ====================

    private static void RunRealConfig(Action<string, Action> run)
    {
        run("配置保存:组的 IsWritable 能写进 JSON 并读回(否则编辑器勾了等于没勾)", () =>
        {
            // 必须放在独立目录:其它用例会把 _work 当成"现场配置目录"整目录加载,
// 往那儿丢一个 json 就会改变它们的设备/文件计数。
            var dir = Path.Combine(AppContext.BaseDirectory, "_jog_work");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "jog_writable_roundtrip.json");

            var project = new ProjectConfig { ProjectName = "P1" };
            var dev = new DeviceConfig { DeviceName = "D1", ProtocolKey = "ModbusTCP" };

            var gWrite = Group("状态值", "1.000001", true, ("Run", "Bool", "1.000001"));
            var gRead = Group("实时组", "1.300001", false, ("T1", "Float", "1.300001"));

            dev.GroupList.Add(gWrite);
            dev.GroupList.Add(gRead);
            project.DevicesByProtocol = new Dictionary<string, List<DeviceConfig>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ModbusTCP"] = new List<DeviceConfig> { dev }
            };

            SettingsJsonCodec.SaveFile(path, new[] { project });

            var json = File.ReadAllText(path);

            TestHelpers.AssertTrue(json.Contains("\"IsWritable\""), "应写入 IsWritable 字段");

            var back = SettingsJsonCodec.LoadFile(path);
            var devBack = back[0].AllDevices().First();

            var gBack = devBack.GroupList.First(x => x.GroupName == "状态值");
            var rBack = devBack.GroupList.First(x => x.GroupName == "实时组");

            TestHelpers.AssertTrue(gBack.IsWritable, "状态值组应保持可写");
            TestHelpers.AssertTrue(!rBack.IsWritable, "实时组不应变成可写");

            // 读回后候选集规则应当立即生效
            var points = JogPoints.Select(devBack, _ => true);

            TestHelpers.AssertEq(1, points.Count, "应只有状态值组的 1 个 Bool 候选");
            TestHelpers.AssertTrue(points[0].IsBool, "候选应是 Bool");
        });

        run("现场配置:设定值组 24 个候选、状态值组默认不开放", () =>
        {
            var dir = FindRealSettingsDir();
            if (dir is null) return;

            // 关键:必须取**现场实际在跑的那台**(RTU Over TCP,139 个变量)。
            // 早先按 ModbusTCP.json 取,那是另一份 129 变量的老配置,结论会偏。
            var loaded = SettingsJsonCodec.LoadDirectory(dir);

            var dev = loaded.Project.AllDevices()
                .FirstOrDefault(d => string.Equals(d.ProtocolKey, "ModbusRTUOverTCP", StringComparison.OrdinalIgnoreCase))
                ?? loaded.Project.AllDevices().First();

            var gate = new CommandGate { Enabled = true, Writer = new FakeWriter(), MinInterval = TimeSpan.Zero };

            // 与界面完全一致的判定:组开放 + 协议可写
            var varGroup = new Dictionary<string, GroupConfig>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in dev.GroupList)
            {
                if (g.VarList is null) continue;

                foreach (var v in g.VarList)
                {
                    if (!string.IsNullOrEmpty(v.VarName)) varGroup[v.VarName] = g;
                }
            }

            gate.BuildWritables(
                dev,
                v => varGroup.TryGetValue(v.VarName, out var g)
                     && JogPoints.IsWritableGroup(g)
                     && ModbusAcquisitionService.CanWrite(g.Start, v, out _),
                v => varGroup.TryGetValue(v.VarName, out var g)
                     ? (JogPoints.IsWritableGroup(g) ? "协议不支持写入" : "组「" + g.GroupName + "」未开放写入")
                     : "找不到所属组");

            var points = JogPoints.Select(dev, gate.IsWritable);
            var bools = points.Count(p => p.IsBool);

            Console.WriteLine("        现场候选点 " + points.Count + " 个,其中 Bool " + bools + " 个");

            foreach (var p in points.Take(3))
            {
                Console.WriteLine("          [" + p.GroupName + "] " + p.VarName + " " + p.VarType);
            }

            // 现场「设定值」是唯一按组名约定开放的组(24 个 Float)
            TestHelpers.AssertEq(24, points.Count, "现场应有 24 个可写候选");
            TestHelpers.AssertEq(0, bools, "现场 Bool 都在未授权的状态值组,候选里应为 0");

            // 授权状态值组后,52 个 Bool 全部可点动
            foreach (var g in dev.GroupList)
            {
                if (g.GroupName != null && g.GroupName.Contains("状态值")) g.IsWritable = true;
            }

            gate.BuildWritables(
                dev,
                v => varGroup.TryGetValue(v.VarName, out var g)
                     && JogPoints.IsWritableGroup(g)
                     && ModbusAcquisitionService.CanWrite(g.Start, v, out _),
                v => "不可写");

            var after = JogPoints.Select(dev, gate.IsWritable);
            var afterBools = after.Count(p => p.IsBool);

            Console.WriteLine("        授权状态值组后: 候选 " + after.Count + " 个,Bool " + afterBools + " 个");

            TestHelpers.AssertEq(52, afterBools, "状态值组 52 个 Bool 应全部成为候选");
            TestHelpers.AssertTrue(JogPoints.EStopTargets(after).Count == 52, "急停目标应为 52 个");
        });
    }

    private static string? FindRealSettingsDir()
    {
        var baseDir = AppContext.BaseDirectory;

        var probe = Path.Combine(baseDir, "_work");
        if (Directory.Exists(probe) && Directory.GetFiles(probe, "*.json").Length > 0) return probe;

        var appDir = Path.Combine(baseDir, "..", "..", "..", "..", "KyjScada.App", "bin", "Debug", "net8.0-windows", "settings");

        return Directory.Exists(appDir) ? appDir : null;
    }
}