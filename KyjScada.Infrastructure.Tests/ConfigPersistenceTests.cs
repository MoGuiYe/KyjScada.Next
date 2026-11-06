using KyjScada.Domain.Config;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 配置持久化测试:多文件加载时记住设备来源、按设备写回字节序。
///
/// 现场诉求:"字节序选了以后要记住,下次启动还是这个"。
/// 这要求写回**该设备自己的文件** —— 合并加载后不看来源就不知道该写哪个。
/// </summary>
public static class ConfigPersistenceTests
{
    public static void RunAll(Action<string, Action> run)
    {
        run("持久化:多文件加载能记住设备来源文件", () =>
        {
            var dir = MakeDir("src");

            var f1 = Path.Combine(dir, "AAAA.json");
            var f2 = Path.Combine(dir, "BBBB.json");

            SettingsJsonCodec.SaveFile(f1, new[] { MakeProject("P1", MakeDevice("设备一", "ModbusTCP")) });
            SettingsJsonCodec.SaveFile(f2, new[] { MakeProject("P2", MakeDevice("设备二", "ModbusRTU")) });

            var loaded = SettingsJsonCodec.LoadDirectory(dir);

            TestHelpers.AssertEq(2, loaded.Project.AllDevices().Count(), "应合并 2 台设备");
            TestHelpers.AssertEq(2, loaded.DeviceFiles.Count, "应记录 2 条来源");

            TestHelpers.AssertTrue(loaded.DeviceFiles.ContainsKey("设备一"), "设备一应有来源");
            TestHelpers.AssertStr(f1, loaded.DeviceFiles["设备一"], "设备一应指向 AAAA.json");
            TestHelpers.AssertStr(f2, loaded.DeviceFiles["设备二"], "设备二应指向 BBBB.json");
        });

        run("持久化:按设备写回字节序并往返生效", () =>
        {
            var dir = MakeDir("write");

            var f = Path.Combine(dir, "ModbusTCP.json");

            SettingsJsonCodec.SaveFile(f, new[] { MakeProject("P", MakeDevice("设备一", "ModbusTCP")) });

            // 默认 ABCD
            var before = SettingsJsonCodec.ActiveProject(SettingsJsonCodec.LoadFile(f))!
                .AllDevices().First();
            TestHelpers.AssertTrue(before.WordOrder == WordOrder.Abcd, "初始应为 ABCD");

            var ok = SettingsJsonCodec.TryUpdateDeviceWordOrder(f, "设备一", WordOrder.Cdab);
            TestHelpers.AssertTrue(ok, "写回应成功");

            // 重新加载应变成 CDAB
            var after = SettingsJsonCodec.ActiveProject(SettingsJsonCodec.LoadFile(f))!
                .AllDevices().First();

            TestHelpers.AssertTrue(after.WordOrder == WordOrder.Cdab,
                "重新加载应为 CDAB,实际 " + after.WordOrder);
        });

        run("持久化:写回不存在的设备返回 false", () =>
        {
            var dir = MakeDir("missing");
            var f = Path.Combine(dir, "ModbusTCP.json");

            SettingsJsonCodec.SaveFile(f, new[] { MakeProject("P", MakeDevice("设备一", "ModbusTCP")) });

            TestHelpers.AssertTrue(
                !SettingsJsonCodec.TryUpdateDeviceWordOrder(f, "不存在的设备", WordOrder.Cdab),
                "应为 false");
        });

        run("持久化:写回不存在的文件返回 false 不抛异常", () =>
        {
            TestHelpers.AssertTrue(
                !SettingsJsonCodec.TryUpdateDeviceWordOrder(
                    Path.Combine(AppContext.BaseDirectory, "_work", "no_such_file.json"),
                    "X", WordOrder.Cdab),
                "应为 false");
        });

        run("持久化:写回会生成 .bak 备份", () =>
        {
            var dir = MakeDir("bak");
            var f = Path.Combine(dir, "ModbusTCP.json");

            SettingsJsonCodec.SaveFile(f, new[] { MakeProject("P", MakeDevice("设备一", "ModbusTCP")) });

            // 第一次写回:此时已存在原文件,应产生 .bak
            SettingsJsonCodec.TryUpdateDeviceWordOrder(f, "设备一", WordOrder.Cdab);

            TestHelpers.AssertTrue(File.Exists(f + ".bak"), "应生成 .bak 备份");
        });

        run("持久化:字节序写回后与合并加载结果一致", () =>
        {
            var dir = MakeDir("consistency");

            var f1 = Path.Combine(dir, "A.json");
            var f2 = Path.Combine(dir, "B.json");

            SettingsJsonCodec.SaveFile(f1, new[] { MakeProject("P1", MakeDevice("设备一", "ModbusTCP")) });
            SettingsJsonCodec.SaveFile(f2, new[] { MakeProject("P2", MakeDevice("设备二", "ModbusTCP")) });

            var loaded = SettingsJsonCodec.LoadDirectory(dir);

            var file1 = loaded.DeviceFiles["设备一"];

            SettingsJsonCodec.TryUpdateDeviceWordOrder(file1, "设备一", WordOrder.Badc);

            // 再次合并加载:设备一应为 BADC,设备二不受影响
            var again = SettingsJsonCodec.LoadDirectory(dir);

            var d1 = again.Project.AllDevices().First(d => d.DeviceName == "设备一");
            var d2 = again.Project.AllDevices().First(d => d.DeviceName == "设备二");

            TestHelpers.AssertTrue(d1.WordOrder == WordOrder.Badc, "设备一应为 BADC");
            TestHelpers.AssertTrue(d2.WordOrder == WordOrder.Abcd, "设备二应仍为 ABCD(未被误改)");
        });
    }

    private static string MakeDir(string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "_work", "cfg_" + name);

        if (Directory.Exists(dir)) Directory.Delete(dir, true);

        Directory.CreateDirectory(dir);

        return dir;
    }

    private static ProjectConfig MakeProject(string name, DeviceConfig device)
    {
        var p = new ProjectConfig { ProjectName = name };

        if (!p.DevicesByProtocol.TryGetValue(device.ProtocolKey, out var list))
        {
            list = new List<DeviceConfig>();
            p.DevicesByProtocol[device.ProtocolKey] = list;
        }

        list.Add(device);

        return p;
    }

    private static DeviceConfig MakeDevice(string name, string protocol)
    {
        var d = new DeviceConfig
        {
            DeviceName = name,
            ProtocolKey = protocol,
            Connection = new ConnectionConfig
            {
                Kind = protocol == "ModbusTCP" ? ConnectionKind.Tcp : ConnectionKind.Serial,
                IpAddress = protocol == "ModbusTCP" ? "127.0.0.1" : "",
                Port = 502,
                PortName = protocol == "ModbusTCP" ? "" : "COM1",
                BaudRate = 9600
            }
        };

        d.GroupList.Add(new GroupConfig
        {
            GroupName = "G",
            Start = "1.400001",
            Length = 10,
            IsActive = true
        });

        return d;
    }
}