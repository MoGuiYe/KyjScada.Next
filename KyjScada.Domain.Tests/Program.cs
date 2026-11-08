using System.Text;
using KyjScada.Domain.Config;
using KyjScada.Domain.Tests;

Console.OutputEncoding = Encoding.UTF8;

// 现场配置文件的位置**不能写死绝对路径**。原来这里钉的是
// @"D:\DeepSeekHarness\KYJScadaProWpf\...\settings" —— 换台机器、换个用户名、
// 或者只是把仓库挪个目录,整套用例就全红,而且红的原因跟被测代码毫无关系。
// 现在按"环境变量 → 本仓库 App 的 settings → 测试目录 _work → 老仓库"依次探测。
var settingsDir = FindSettingsDir ();

var workDir = Path.Combine(AppContext.BaseDirectory, "_work");
Directory.CreateDirectory(workDir);

var t = new TestRunner();

t.Run("文件存在", () =>
{
    var p = SettingsFile ("ModbusTCP.json");
    Assert.True(File.Exists(p), "找不到 " + p);
    Assert.True(new FileInfo(p).Length > 0, "配置文件是空文件: " + p);
});

t.Run("解析 ModbusTCP 项目", () =>
{
    var projects = SettingsJsonCodec.LoadFile(SettingsFile ("ModbusTCP.json"));
    var project = SettingsJsonCodec.ActiveProject(projects);

    Assert.True(project is not null, "应能找到有效项目");
    Assert.Equal("ModbusTCP空压机", project!.ProjectName, "项目名");

    var tcp = project.GetDevices("ModbusTCP");
    Assert.Equal(1, tcp.Count, "ModbusTCP 设备数");

    var dev = tcp[0];
    Assert.Equal("空压机ModbusTCP", dev.DeviceName, "设备名");
    Assert.Equal(ConnectionKind.Tcp, dev.Connection.Kind, "连接类型");
    Assert.Equal("127.0.0.1", dev.Connection.IpAddress, "IP");
    Assert.Equal(502, dev.Connection.Port, "端口");
    Assert.Equal(4, dev.GroupList.Count, "组数");
});

t.Run("设备参数进 Options 而非 Comments", () =>
{
    var projects = SettingsJsonCodec.LoadFile(SettingsFile ("ModbusTCP.json"));
    var dev = SettingsJsonCodec.ActiveProject(projects)!.GetDevices("ModbusTCP")[0];

    Assert.True(dev.Options.ContainsKey("IsShortAddress"), "Options 应有 IsShortAddress");
    Assert.Equal("True", dev.Options["IsShortAddress"], "短地址值");

    var firstVar = dev.GroupList[0].VarList[0];
    Assert.Equal("冷却塔液位", firstVar.Comments, "备注不能被参数覆盖");
});

t.Run("变量字段完整", () =>
{
    var projects = SettingsJsonCodec.LoadFile(SettingsFile ("ModbusTCP.json"));
    var v = SettingsJsonCodec.ActiveProject(projects)!.GetDevices("ModbusTCP")[0].GroupList[0].VarList[0];

    Assert.Equal("LQT_Level", v.VarName, "变量名");
    Assert.Equal("Float", v.VarType, "类型");
    Assert.Equal("0", v.Start, "组内偏移");
    Assert.Equal("1.40001", v.VarAddress, "完整地址");
    Assert.Near(1.0, v.Scale, "Scale");
    Assert.Near(0.0, v.Offset, "Offset");
    Assert.True(v.ArchiveEnable, "归档");
    Assert.True(v.HighAlarmEnable, "高报启用");
    Assert.Near(54.0, v.HighAlarmValue, "高报值");
    Assert.True(v.LowAlarmEnable, "低报启用");
    Assert.Near(5.0, v.LowAlarmValue, "低报值");
});

t.Run("Scale 为 0 时修正为 1", () =>
{
    var json = "[{\"ProjectName\":\"P\",\"ModbusTCPList\":[{\"DeviceName\":\"D\",\"IpAddress\":\"1.1.1.1\",\"Port\":502,\"GroupList\":[{\"GroupName\":\"G\",\"VarList\":[{\"VarName\":\"V\",\"VarType\":\"Float\",\"Scale\":0}]}]}]}]";

    var projects = SettingsJsonCodec.Parse(json);
    var v = SettingsJsonCodec.ActiveProject(projects)!.GetDevices("ModbusTCP")[0].GroupList[0].VarList[0];

    Assert.Near(1.0, v.Scale, "Scale 0 应修正为 1,否则读数恒为 0");
});

t.Run("串口设备解析", () =>
{
    var projects = SettingsJsonCodec.LoadFile(SettingsFile ("ModbusRTU.json"));
    var rtu = SettingsJsonCodec.ActiveProject(projects)!.GetDevices("ModbusRTU");

    Assert.Equal(1, rtu.Count, "RTU 设备数");

    var dev = rtu[0];
    Assert.Equal(ConnectionKind.Serial, dev.Connection.Kind, "连接类型");
    Assert.Equal("COM8", dev.Connection.PortName, "串口名");
    Assert.Equal(9600, dev.Connection.BaudRate, "波特率");
    Assert.Equal(8, dev.Connection.DataBits, "数据位");
});

t.Run("往返幂等", () =>
{
    var path = SettingsFile ("ModbusTCP.json");

    var original = SettingsJsonCodec.LoadFile(path);
    var json1 = SettingsJsonCodec.ToJson(original);
    var again = SettingsJsonCodec.Parse(json1);
    var json2 = SettingsJsonCodec.ToJson(again);

    Assert.Equal(json1, json2, "两次序列化不一致,存在不稳定字段");

    var p1 = SettingsJsonCodec.ActiveProject(original)!;
    var p2 = SettingsJsonCodec.ActiveProject(again)!;

    var c1 = p1.AllVariables().Count();
    var c2 = p2.AllVariables().Count();
    Assert.Equal(c1, c2, "变量数往返后变化");

    var list1 = p1.AllVariables().ToList();
    var list2 = p2.AllVariables().ToList();

    for (int i = 0; i < list1.Count; i++)
    {
        Assert.Equal(list1[i].VarName, list2[i].VarName, "第 " + i + " 个变量名");
        Assert.Equal(list1[i].VarAddress, list2[i].VarAddress, "第 " + i + " 个地址");
        Assert.Near(list1[i].Scale, list2[i].Scale, "第 " + i + " 个 Scale");
        Assert.Near(list1[i].HighAlarmValue, list2[i].HighAlarmValue, "第 " + i + " 个高报值");
    }
});

t.Run("往返 ModbusRTU 全字段", () =>
{
    var path = SettingsFile ("ModbusRTU.json");

    var original = SettingsJsonCodec.LoadFile(path);
    var again = SettingsJsonCodec.Parse(SettingsJsonCodec.ToJson(original));

    var v1 = SettingsJsonCodec.ActiveProject(original)!.AllVariables().ToList();
    var v2 = SettingsJsonCodec.ActiveProject(again)!.AllVariables().ToList();

    Assert.Equal(v1.Count, v2.Count, "变量总数");

    for (int i = 0; i < v1.Count; i++)
    {
        Assert.Equal(v1[i].VarName, v2[i].VarName, "[" + i + "] VarName");
        Assert.Equal(v1[i].VarType, v2[i].VarType, "[" + i + "] VarType");
        Assert.Equal(v1[i].Start, v2[i].Start, "[" + i + "] Start");
        Assert.Equal(v1[i].VarAddress, v2[i].VarAddress, "[" + i + "] VarAddress");
        Assert.Equal(v1[i].Comments, v2[i].Comments, "[" + i + "] Comments");
        Assert.Near(v1[i].Scale, v2[i].Scale, "[" + i + "] Scale");
        Assert.Near(v1[i].Offset, v2[i].Offset, "[" + i + "] Offset");
        Assert.Equal(v1[i].ArchiveEnable, v2[i].ArchiveEnable, "[" + i + "] Archive");
        Assert.Near(v1[i].HighAlarmValue, v2[i].HighAlarmValue, "[" + i + "] 高报值");
        Assert.Near(v1[i].LowAlarmValue, v2[i].LowAlarmValue, "[" + i + "] 低报值");
    }
});

t.Run("原子写不损坏原文件", () =>
{
    var src = SettingsFile ("ModbusTCP.json");
    var dst = Path.Combine(workDir, "ModbusTCP.json");

    File.Copy(src, dst, true);

    var projects = SettingsJsonCodec.LoadFile(dst);
    SettingsJsonCodec.SaveFile(dst, projects);

    Assert.True(File.Exists(dst), "保存后文件应存在");
    Assert.True(!File.Exists(dst + ".tmp"), "临时文件应已被替换");

    var reread = SettingsJsonCodec.LoadFile(dst);
    var before = SettingsJsonCodec.ActiveProject(projects)!.AllVariables().Count();
    var after = SettingsJsonCodec.ActiveProject(reread)!.AllVariables().Count();

    Assert.Equal(before, after, "保存后变量数");
});

t.Run("未知协议键不丢", () =>
{
    var json = "[{\"ProjectName\":\"P\",\"FutureProtocolList\":[{\"DeviceName\":\"X\",\"IpAddress\":\"1.2.3.4\",\"Port\":502,\"GroupList\":[]}]}]";

    var projects = SettingsJsonCodec.Parse(json);
    var devices = projects[0].GetDevices("FutureProtocol");

    Assert.Equal(1, devices.Count, "未知协议设备应被保留");

    var back = SettingsJsonCodec.ToJson(projects);
    Assert.Contains(back, "FutureProtocolList", "未知协议键应写回");
});

t.Run("3 个现场文件都能解析出有效项目", () =>
{
    foreach (var name in new[] { "ModbusTCP.json", "ModbusRTU.json", "ModbusRTUOverTCP.json" })
    {
        var p = SettingsFile (name);
        Assert.True(File.Exists(p), "缺少 " + name);

        var projects = SettingsJsonCodec.LoadFile(p);
        var project = SettingsJsonCodec.ActiveProject(projects);

        Assert.True(project is not null, name + " 找不到有效项目");

        var devCount = project!.AllDevices().Count();
        var varCount = project.AllVariables().Count();

        Assert.True(devCount >= 1, name + " 无设备");
        Assert.True(varCount >= 100, name + " 变量数只有 " + varCount);
    }
});

t.Run("3 个文件往返后变量数一致", () =>
{
    foreach (var name in new[] { "ModbusTCP.json", "ModbusRTU.json", "ModbusRTUOverTCP.json" })
    {
        var p = SettingsFile (name);

        var original = SettingsJsonCodec.LoadFile(p);
        var json = SettingsJsonCodec.ToJson(original);
        var again = SettingsJsonCodec.Parse(json);

        var c1 = SettingsJsonCodec.ActiveProject(original)!.AllVariables().Count();
        var c2 = SettingsJsonCodec.ActiveProject(again)!.AllVariables().Count();

        Assert.Equal(c1, c2, name + " 往返变量数");
    }
});

ProtocolTests.RunAll(t);
AlarmTests.RunAll(t);

return t.Report();

/// <summary>
/// 找现场配置目录。探测顺序:环境变量 KYJ_SETTINGS_DIR → 本仓库 App 的
/// bin\...\settings → 测试输出目录的 _work → 老系统仓库。都找不到返回 null,
/// 由 SettingsFile 抛 SkipTestException(记 SKIP,不冒充 PASS)。
/// </summary>
string? FindSettingsDir()
{
    var fromEnv = Environment.GetEnvironmentVariable("KYJ_SETTINGS_DIR");

    if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv)) return fromEnv;

    var baseDir = AppContext.BaseDirectory;

    var candidates = new List<string>
    {
        // 本仓库:从 <repo>\KyjScada.Domain.Tests\bin\Debug\net8.0 往上找 App 的输出目录
        Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
            "KyjScada.App", "bin", "Debug", "net8.0-windows", "settings")),

        // 用例自己搬过来的副本
        Path.Combine(baseDir, "_work")
    };

    foreach (var dir in candidates)
    {
        if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "ModbusTCP.json"))) return dir;
    }

    // 最后才退回老仓库(只在开发机上存在,不再当唯一路径)
    const string legacy = @"D:\DeepSeekHarness\KYJScadaProWpf\KYJScadaProWpf\bin\Debug\settings";

    if (Directory.Exists(legacy) && File.Exists(Path.Combine(legacy, "ModbusTCP.json"))) return legacy;

    return null;
}

/// <summary>
/// 取一个现场配置文件的完整路径。
/// 连目录都没有 = 环境不具备 → SKIP;目录有、文件缺 = 仓库缺东西 → 真失败。
/// </summary>
string SettingsFile(string name)
{
    if (settingsDir is null)
    {
        throw new SkipTestException(
            "找不到现场配置目录(可用环境变量 KYJ_SETTINGS_DIR 指定)。这套用例依赖现场变量表,不伪造数据。");
    }

    var path = Path.Combine(settingsDir, name);

    if (!File.Exists(path)) throw new Exception("配置目录 " + settingsDir + " 里缺少 " + name);

    return path;
}
