using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Domain.History;
using KyjScada.Domain.Users;
using KyjScada.Infrastructure.Config;
using KyjScada.Infrastructure.Modbus;
using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 本轮审查修复的回归用例。
///
/// 每条都对应一个"测试全绿但还是会出事"的缺陷:权限链、配置静默改写、
/// 归档补写与清理原因、采样间隔取样、MBAP 单元号。
/// 写在这里而不是各归各家,是为了让"这条为什么存在"一句话就能查到。
/// </summary>
public static class RegressionTests
{
    public static void RunAll(Action<string, Action> run)
    {
        RunWritePermission(run);
        RunTrendInterval(run);
        RunValidator(run);
        RunArchive(run);
        RunFrame(run);
    }

    // ==================== 写入权限链 ====================

    private static void RunWritePermission(Action<string, Action> run)
    {
        run("权限:急停(estop)必须 CanForce,点动(CanJog)不够", () =>
        {
            var gate = MakeGate(held: PermissionBit.CanJog);

            // 只有点动权限:点动能过,hmi/急停都不能过
            gate.Writer = new FakeWriter();

            var jog = gate.Write("B1", 1.0, "jog");
            TestHelpers.AssertTrue(jog.Success, "有点动权限应能点动,实际 " + jog.Verdict + " / " + jog.Message);

            var estop = gate.Write("B1", 0.0, "estop", null, confirmed: true);
            TestHelpers.AssertTrue(estop.Verdict == WriteVerdict.Denied,
                "急停没有 CanForce 必须被拒(急停是整片写 0,不能等同点动),实际 " + estop.Verdict);

            var hmi = gate.Write("SET1", 5.0, "hmi");
            TestHelpers.AssertTrue(hmi.Verdict == WriteVerdict.Denied,
                "没有 CanWrite 不能写设定值,实际 " + hmi.Verdict);
        });

        run("权限:注入 Authorizer 后,写入口在闸门内被拦(不靠各页面自觉判)", () =>
        {
            var gate = MakeGate(held: PermissionBit.CanWrite);
            gate.Writer = new FakeWriter();

            var ok = gate.Write("SET1", 5.0, "hmi");
            TestHelpers.AssertTrue(ok.Success, "有 CanWrite 应能写,实际 " + ok.Verdict + " / " + ok.Message);

            // TryEnter 是 public 的,绕过 WriteAsync 也必须查权限
            var byPas = gate.TryEnter("SET1", "estop", 0.0);
            TestHelpers.AssertTrue(byPas is not null, "TryEnter 也必须校验权限,不能绕过闸门");
        });

        run("权限:未注入 Authorizer 时行为不变(单测/嵌入场景)", () =>
        {
            var gate = new CommandGate { Enabled = true };

            gate.BuildWritables(MakeDevice("SET1"), _ => true);
            gate.Writer = new FakeWriter();

            var r = gate.Write("SET1", 1.0, "estop");

            TestHelpers.AssertTrue(r.Success, "没有用户体系时不应被判无权限,实际 " + r.Verdict);
        });
    }

    // ==================== 趋势:中位采样间隔 ====================

    private static void RunTrendInterval(Action<string, Action> run)
    {
        run("趋势:超过 4096 点时中位间隔不被取样步长放大", () =>
        {
            // 10000 个点、真实间隔 1 秒。取样步长会是 2,差值跨 2 个点 = 2 秒。
            // 不除回步长的话中位间隔读成 2 秒,自动粒度被放大一倍,趋势把波动抹平。
            var t0 = new DateTime(2026, 11, 4, 0, 0, 0);
            var raw = new List<HistoryRecord>();

            for (var i = 0; i < 10000; i++)
            {
                raw.Add(new HistoryRecord(t0.AddSeconds(i), 1, i, HistoryQuality.Good));
            }

            var median = TrendAggregator.MedianInterval(raw);

            TestHelpers.AssertTrue(Math.Abs(median - 1.0) < 1e-6,
                "中位间隔应为 1 秒(真实采样率),实际 " + median);
        });
    }

    // ==================== 配置校验 ====================

    private static void RunValidator(Action<string, Action> run)
    {
        run("配置校验:Scale 为 NaN 必须报错(否则所有读数变 NaN)", () =>
        {
            var v = Var("V1");
            v.Scale = float.NaN;

            var problems = ConfigValidator.Validate(Project(Device("D1", Group("G1", 10, v))));

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("Scale")),
                "应报 Scale 非数值,实际: " + string.Join(" | ", problems));
        });

        run("配置校验:死区为 NaN 必须报错(否则报警恒不成立)", () =>
        {
            var v = Var("V1");
            v.AlarmDeadband = float.NaN;

            var problems = ConfigValidator.Validate(Project(Device("D1", Group("G1", 10, v))));

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("死区")),
                "应报死区非数值,实际: " + string.Join(" | ", problems));
        });

        run("配置校验:跨设备同名变量必须报错(历史库按变量名全局唯一)", () =>
        {
            var problems = ConfigValidator.Validate(Project(
                Device("D1", Group("G1", 10, Var("同名"))),
                Device("D2", Group("G2", 10, Var("同名")))));

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("跨设备")),
                "跨设备重名会让两个测点写进同一个 VarId,必须报,实际: " + string.Join(" | ", problems));
        });

        run("配置校验:组起始+长度超出 16 位地址空间必须报错", () =>
        {
            // 1.465536 → 偏移 65535(16 位地址空间的最后一个),长度 10 → 65545 > 65536。
            // 注意不能用 1.65530 之类:ParseGroupStart 只认 400001..465536 / 40001..49999,
            // 那个值会先被判成"起始地址无法解析",根本走不到这条检查。
            var problems = ConfigValidator.Validate(Project(Device("D1", Group("G1", 10, Var("V1"), start: "1.465536"))));

            TestHelpers.AssertTrue(problems.Any(x => x.Contains("16 位地址空间")),
                "组越界运行期整组失败,配置期必须报,实际: " + string.Join(" | ", problems));
        });
    }

    // ==================== 归档补写与清理原因 ====================

    private static void RunArchive(Action<string, Action> run)
    {
        run("归档:本批无有效点时仍要补写上一批失败的点", () =>
        {
            var repo = new FakeRepo { FailFirstWrite = true };
            var svc = new ArchiveService(repo);
            var dev = MakeDevice("V1");

            svc.BuildFrom(dev);

            var t = new DateTime(2026, 11, 4, 10, 0, 0);

            // 第 1 批:有值但写库失败 → 进 _pending
            svc.Archive(Snapshot(("V1", 5.5, TagQuality.Good), t), dev);

            TestHelpers.AssertTrue(svc.LastError is { Length: > 0 }, "第一次应记录写库失败");

            // 第 2 批:全部 NotCollected(掉线),本批没有有效点。
            // 早先这里直接 return,补写永远等不到机会。
            var written = svc.Archive(Snapshot(("V1", null, TagQuality.NotCollected), t.AddSeconds(10)), dev);

            TestHelpers.AssertEq(1, written, "上一批的点应在本轮补写成功");
            TestHelpers.AssertTrue(svc.LastError is null, "补写成功后不应再有错误,实际 " + svc.LastError);
        });

        run("归档:写库成功后不能抹掉清理失败的原因", () =>
        {
            var repo = new FakeRepo { ThrowOnDelete = true };
            var svc = new ArchiveService(repo);
            var dev = MakeDevice("V1");

            svc.BuildFrom(dev);

            var t = new DateTime(2026, 11, 4, 10, 0, 0);

            svc.Purge(t, 30);   // 清理失败
            TestHelpers.AssertTrue(svc.LastError is { Length: > 0 }, "清理失败应记下来");

            svc.Archive(Snapshot(("V1", 5.5, TagQuality.Good), t), dev);   // 写库成功

            TestHelpers.AssertTrue(svc.LastError is { Length: > 0 },
                "写库成功不应抹掉清理失败的原因(否则库只涨不跌还显示正常)");
        });
    }

    // ==================== MBAP 单元号 ====================

    private static void RunFrame(Action<string, Action> run)
    {
        run("Modbus:TCP 响应单元号与请求不一致必须拒绝", () =>
        {
            var pdu = new byte[] { ModbusFunction.ReadHoldingRegisters, 2, 0x00, 0x2A };

            // 回帧的单元号是 2,而我们请求的是 1
            var frame = ModbusFrame.BuildFrame(7, 2, pdu);

            var threw = false;

            try
            {
                ModbusFrame.ParseResponse(frame, 7, expectedUnitId: 1);
            }
            catch (ModbusException)
            {
                threw = true;
            }

            TestHelpers.AssertTrue(threw, "单元号不匹配必须抛异常,否则会把别台从站的数据当成自己的");
        });
    }

    // ==================== 辅助 ====================

    private static CommandGate MakeGate(PermissionBit held)
    {
        var gate = new CommandGate
        {
            Enabled = true,
            MinInterval = TimeSpan.Zero,
            Authorizer = p => (held & p) == p
        };

        gate.BuildWritables(MakeDevice("SET1", "B1"), _ => true);

        return gate;
    }

    private sealed class FakeWriter : IDeviceWriter
    {
        public bool WriteVariable(string varName, double value, out string error)
        {
            error = "";

            return true;
        }
    }

    private sealed class FakeRepo : IHistoryRepository
    {
        public bool FailFirstWrite { get; init; }
        public bool ThrowOnDelete { get; init; }

        private int _writes;

        public int WritePoints(IReadOnlyList<HistoryPoint> points)
        {
            _writes++;

            if (FailFirstWrite && _writes == 1) throw new InvalidOperationException("库被锁");

            return points.Count;
        }

        public int DeleteBefore(DateTime cutoff)
        {
            if (ThrowOnDelete) throw new InvalidOperationException("磁盘满");

            return 0;
        }
    }

    private static DeviceConfig MakeDevice(params string[] varNames)
    {
        var device = new DeviceConfig
        {
            DeviceName = "D1",
            ProtocolKey = "ModbusTCP",
            Connection = new ConnectionConfig { Kind = ConnectionKind.Tcp, IpAddress = "127.0.0.1", Port = 502 }
        };

        var group = new GroupConfig { GroupName = "G1", IsActive = true, Start = "1.400001", Length = 20 };

        foreach (var name in varNames)
        {
            group.VarList.Add(new VariableConfig
            {
                // Bool 点既能点动也能当写入目标;设定值点用 Float
                VarName = name,
                VarType = name.StartsWith("B") ? "Bool" : "Float",
                Start = "1.400001",
                VarAddress = "1.400001",
                ArchiveEnable = true
            });
        }

        device.GroupList.Add(group);

        return device;
    }

    private static ValueSnapshot Snapshot((string Name, double? Value, TagQuality Quality) item, DateTime at)
    {
        var dict = new Dictionary<string, TagValue>(StringComparer.OrdinalIgnoreCase)
        {
            [item.Name] = new TagValue(item.Name, item.Value, item.Quality, at)
        };

        return new ValueSnapshot(1, at, true, dict);
    }

    private static ProjectConfig Project(params DeviceConfig[] devices)
    {
        var project = new ProjectConfig { ProjectName = "P" };

        project.DevicesByProtocol["ModbusTCP"] = devices.ToList();

        return project;
    }

    private static DeviceConfig Device(string name, GroupConfig group)
    {
        var device = new DeviceConfig
        {
            DeviceName = name,
            ProtocolKey = "ModbusTCP",
            IsActive = true,
            Connection = new ConnectionConfig { Kind = ConnectionKind.Tcp, IpAddress = "127.0.0.1", Port = 502 }
        };

        device.GroupList.Add(group);

        return device;
    }

    private static GroupConfig Group(string name, int length, VariableConfig v, string start = "1.400001")
    {
        var group = new GroupConfig { GroupName = name, IsActive = true, Start = start, Length = length };

        group.VarList.Add(v);

        return group;
    }

    private static VariableConfig Var(string name)
    {
        return new VariableConfig
        {
            VarName = name,
            VarType = "Float",
            Start = "1.400001",
            VarAddress = "1.400001",
            Scale = 1f
        };
    }
}