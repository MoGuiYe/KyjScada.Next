using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Infrastructure.Acquisition;
using KyjScada.Infrastructure.Config;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 协议分派工厂测试。
///
/// 核心诉求:**配置里出现任何协议都不能让程序崩**。
/// 已实现的正常采集;未实现的要给出明确原因并继续显示配置,
/// 绝不能"静默不采"(那样现场只会看到一片 --,查不出是协议没实现)。
/// </summary>
public static class AcquisitionFactoryTests
{
    public static void RunAll(Action<string, Action> run)
    {
        RunDispatch(run);
        RunUnsupported(run);
        RunRealConfig(run);
    }

    private static DeviceConfig Device(string protocolKey, string name = "D1")
    {
        return new DeviceConfig
        {
            DeviceName = name,
            ProtocolKey = protocolKey,
            Connection = new ConnectionConfig
            {
                Kind = ConnectionKind.Tcp,
                IpAddress = "127.0.0.1",
                Port = 502
            }
        };
    }

    private static void RunDispatch(Action<string, Action> run)
    {
        run("协议分派:ModbusTCP 可创建采集器", () =>
        {
            var plan = AcquisitionFactory.Create(Device("ModbusTCP"));

            TestHelpers.AssertTrue(plan.Supported, "应支持");
            TestHelpers.AssertTrue(plan.Service is not null, "应返回采集器实例");
            TestHelpers.AssertStr("ModbusTCP", plan.ProtocolKey, "协议键");
            TestHelpers.AssertTrue(plan.DisplayName.Length > 0, "应有显示名");

            plan.Service!.Dispose();
        });

        run("协议分派:采集器实现统一接口", () =>
        {
            var plan = AcquisitionFactory.Create(Device("ModbusTCP"));

            TestHelpers.AssertTrue(plan.Service is IAcquisitionService, "应是 IAcquisitionService");

            plan.Service!.Dispose();
        });
    }

    private static void RunUnsupported(Action<string, Action> run)
    {
        run("协议分派:ModbusRTUOverTCP 已实现(RTU 帧跑 TCP 流)", () =>
        {
            TestHelpers.AssertTrue(AcquisitionFactory.IsCollectable("ModbusRTUOverTCP"),
                "RTU over TCP 应可采集");

            var plan = AcquisitionFactory.Create(Device("ModbusRTUOverTCP"));
            TestHelpers.AssertTrue(plan.Supported, "应支持");

            plan.Service?.Dispose();
        });

        run("协议分派:ModbusRTU 已实现(串口)", () =>
        {
            TestHelpers.AssertTrue(AcquisitionFactory.IsCollectable("ModbusRTU"),
                "ModbusRTU 应为可采集");
        });

        run("协议分派:西门子未实现但有显示名", () =>
        {
            var plan = AcquisitionFactory.Create(Device("Siemens"));

            TestHelpers.AssertTrue(!plan.Supported, "不应支持");
            TestHelpers.AssertTrue(plan.DisplayName.Length > 0, "应能取到描述符显示名");
            TestHelpers.AssertTrue(plan.Message.Contains("尚未实现"), "应说明未实现");
        });

        run("协议分派:协议家族判断与实际注册键一致", () =>
        {
            // 描述符实际注册的是 "Siemens"/"Melsec"/"Omron",
            // 家族判断必须认得这些键,否则地址族判错会导致用错功能码
            TestHelpers.AssertTrue(KyjScada.Domain.Protocol.ProtocolKeyMap.IsSiemensFamily("Siemens"),
                "Siemens 应属西门子家族");
            TestHelpers.AssertTrue(KyjScada.Domain.Protocol.ProtocolKeyMap.IsMelsecFamily("Melsec"),
                "Melsec 应属三菱家族");
            TestHelpers.AssertTrue(KyjScada.Domain.Protocol.ProtocolKeyMap.IsMelsecFamily("MelsecFXSerial"),
                "MelsecFXSerial 应属三菱家族");
            TestHelpers.AssertTrue(KyjScada.Domain.Protocol.ProtocolKeyMap.IsOmronFamily("Omron"),
                "Omron 应属欧姆龙家族");
            TestHelpers.AssertTrue(KyjScada.Domain.Protocol.ProtocolKeyMap.IsOmronFamily("OmronHostlink"),
                "OmronHostlink 应属欧姆龙家族");
        });

        run("协议分派:OPC 未实现", () =>
        {
            var plan = AcquisitionFactory.Create(Device("OPCUA"));

            TestHelpers.AssertTrue(!plan.Supported, "不应支持");
            TestHelpers.AssertTrue(plan.Message.Contains("尚未实现"), "应说明未实现");
        });

        run("协议分派:未知协议给出明确原因", () =>
        {
            var plan = AcquisitionFactory.Create(Device("FooProtocol"));

            TestHelpers.AssertTrue(!plan.Supported, "不应支持");
            TestHelpers.AssertTrue(plan.Message.Contains("未知协议"),
                "应报未知协议,实际: " + plan.Message);
        });

        run("协议分派:null 设备不抛异常", () =>
        {
            var plan = AcquisitionFactory.Create(null);

            TestHelpers.AssertTrue(!plan.Supported, "不应支持");
            TestHelpers.AssertTrue(plan.Message.Length > 0, "应给原因");
        });

        run("协议分派:IsCollectable 只对已实现协议为真", () =>
        {
            TestHelpers.AssertTrue(AcquisitionFactory.IsCollectable("ModbusTCP"), "ModbusTCP 应为真");
            TestHelpers.AssertTrue(AcquisitionFactory.IsCollectable("ModbusRTUOverTCP"), "RTUOverTCP 应为真(已实现)");
            TestHelpers.AssertTrue(!AcquisitionFactory.IsCollectable("SiemensS7_1200"), "西门子应为假");
            TestHelpers.AssertTrue(!AcquisitionFactory.IsCollectable(""), "空应为假");
            TestHelpers.AssertTrue(!AcquisitionFactory.IsCollectable(null), "null 应为假");
        });

        run("协议分派:全部已注册协议都不会抛异常", () =>
        {
            // 已实现的协议:TCP + 串口 RTU。其余必须 supported=false 且给出原因。
            var implemented = new[] { "ModbusTCP", "ModbusRTU", "ModbusRTUOverTCP" };

            foreach (var key in KyjScada.Domain.Protocol.ProtocolRegistry.AllKeys())
            {
                var plan = AcquisitionFactory.Create(Device(key));

                if (implemented.Contains(key))
                {
                    TestHelpers.AssertTrue(plan.Supported, key + " 应支持");
                    plan.Service?.Dispose();
                }
                else
                {
                    TestHelpers.AssertTrue(!plan.Supported, key + " 不应支持");
                    TestHelpers.AssertTrue(plan.Message.Length > 0, key + " 应给原因");
                }
            }
        });
    }

    private static void RunRealConfig(Action<string, Action> run)
    {
        run("协议分派:现场 3 个配置文件合并后仅 ModbusTCP 可采", () =>
        {
            var dir = FindSettingsDir();
            if (dir is null) return;

            var loaded = KyjScada.Domain.Config.SettingsJsonCodec.LoadDirectory(dir);

            var devices = loaded.Project.AllDevices().ToList();

            Console.WriteLine("        合并文件 " + loaded.Files.Count + " 个,设备 " + devices.Count + " 台");

            foreach (var d in devices)
            {
                Console.WriteLine("          " + d.DeviceName + " -> " + d.ProtocolKey
                    + "  可采=" + AcquisitionFactory.IsCollectable(d.ProtocolKey));
            }

            var collectable = devices.Count(d => AcquisitionFactory.IsCollectable(d.ProtocolKey));

            // 现场 3 个文件:ModbusTCP + ModbusRTU 可采,RTUOverTCP 不可采
            TestHelpers.AssertEq(3, collectable, "当前应有 3 台可采(TCP + RTU + RTU-over-TCP)");

            // 每个设备都能拿到 plan,且不抛异常
            foreach (var d in devices)
            {
                var plan = AcquisitionFactory.Create(d);
                plan.Service?.Dispose();
            }
        });
    }

    private static string? FindSettingsDir()
    {
        var baseDir = AppContext.BaseDirectory;

        var probe = Path.Combine(baseDir, "_work");
        if (Directory.Exists(probe) && Directory.GetFiles(probe, "*.json").Length > 0) return probe;

        var appDir = Path.Combine(baseDir, "..", "..", "..", "..", "KyjScada.App", "bin", "Debug", "net8.0-windows", "settings");

        return Directory.Exists(appDir) ? appDir : null;
    }
}