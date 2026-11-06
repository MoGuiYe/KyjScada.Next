using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Infrastructure.Acquisition;
using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 活体校验:对**现场实际使用的那台设备**做一次「读 → 写 → 再读」闭环。
///
/// 为什么必须单独做:
///  单元测试里的 ModbusTcpTransport 发的是 MBAP 帧,而现场用的是
///  **Modbus RTU Over TCP**(RTU 帧 + CRC,走 TCP 流)。两者字节格式完全不同,
///  MBAP 测试全绿也不能证明现场那条链路能写。
///  这里直接拿 settings 目录里的设备配置建采集器,走的就是现场同一条实现。
///
/// 用法(需要现场设备/模拟器在线):
///  dotnet run --project KyjScada.Infrastructure.Tests -- --live
/// 不加 --live 不会执行,避免在没设备的机器上误报失败。
/// </summary>
public static class LiveCheck
{
    public static int Run(string settingsDir)
    {
        Console.WriteLine("== 活体校验: " + settingsDir);

        if (!Directory.Exists(settingsDir))
        {
            Console.WriteLine("   跳过: settings 目录不存在");
            return 0;
        }

        var loaded = SettingsJsonCodec.LoadDirectory(settingsDir);
        var device = loaded.Project.AllDevices()
            .FirstOrDefault(d => string.Equals(d.ProtocolKey, "ModbusRTUOverTCP", StringComparison.OrdinalIgnoreCase))
            ?? loaded.Project.AllDevices().FirstOrDefault();

        if (device is null)
        {
            Console.WriteLine("   跳过: 配置里没有设备");
            return 0;
        }

        Console.WriteLine("   设备: " + device.DeviceName
            + "  协议: " + device.ProtocolKey
            + "  连接: " + device.Connection.Describe()
            + "  字序: " + device.WordOrder);

        var plan = AcquisitionFactory.Create(device);

        if (plan.Service is null)
        {
            Console.WriteLine("   跳过: 该协议 v1 不采集(" + plan.Message + ")");
            return 0;
        }

        using var svc = plan.Service;

        var bus = new ValueBus();

        if (!TryCollect(svc, bus, device, out var pick))
        {
            Console.WriteLine("   跳过: 连不上设备(现场没开就没法验)");
            return 0;
        }

        Console.WriteLine("   采集成功,可写候选中取 " + pick.VarName + " 当前=" + pick.Value.ToString("0.###"));

        var writer = svc as IDeviceWriter;

        if (writer is null)
        {
            Console.WriteLine("   [FAIL] 采集器没实现 IDeviceWriter,界面写入功能整体不可用");
            return 1;
        }

        // 用命令闸门,和界面走完全相同的路径(权限/类型/限速/审计)
        var gate = new CommandGate
        {
            Enabled = true,
            Writer = writer,
            MinInterval = TimeSpan.Zero
        };

        var groupOf = new Dictionary<string, GroupConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in device.GroupList)
        {
            if (g.VarList is null) continue;

            foreach (var v in g.VarList)
            {
                if (!string.IsNullOrEmpty(v.VarName)) groupOf[v.VarName] = g;
            }
        }

        gate.BuildWritables(
            device,
            v => groupOf.TryGetValue(v.VarName, out var g)
                 && JogPoints.IsWritableGroup(g)
                 && ModbusAcquisitionService.CanWrite(g.Start, v, out _),
            v => "不可写");

        if (!gate.IsWritable(pick.VarName))
        {
            Console.WriteLine("   [FAIL] " + pick.VarName + " 不在可写集合里");
            return 1;
        }

        var probe = Math.Round(pick.Value + 0.1, 3);

        var w = gate.Write(pick.VarName, probe, "livecheck");

        Console.WriteLine("   写入 " + pick.VarName + " = " + probe.ToString("0.###") + " -> " + (w.Success ? "成功" : "失败: " + w.Message));

        if (!w.Success) return 1;

        // 写后立刻再采一轮,确认设备里真的是新值(这才是写入落地的证据)
        var back = ReadOnce(svc, bus, pick.VarName);
        var sawNew = back.HasValue && Math.Abs(back.Value - probe) < 0.05;

        Console.WriteLine("   读回 " + (back.HasValue ? back.Value.ToString("0.###") : "无值")
            + (sawNew ? "  ✔ 与写入一致" : "  ✘ 与写入不一致"));

        if (!sawNew) return 1;

        // 还回原值,不把现场设备的设定值改掉
        var restore = gate.Write(pick.VarName, pick.Value, "livecheck");

        if (!restore.Success)
        {
            Console.WriteLine("   [警告] 原值没能还回去: " + restore.Message);
        }

        var back2 = ReadOnce(svc, bus, pick.VarName);
        var restored = back2.HasValue && Math.Abs(back2.Value - pick.Value) < 0.05;

        Console.WriteLine("   还原 " + (back2.HasValue ? back2.Value.ToString("0.###") : "无值")
            + (restored ? "  ✔ 已还原" : "  ✘ 仍是探针值"));

        Console.WriteLine("   审计条数: " + gate.Audit.Count);
        Console.WriteLine("== 活体校验结果: " + (restored ? "通过" : "失败"));

        return restored ? 0 : 1;
    }

    private sealed record Pick(string VarName, double Value);

    private static bool TryCollect(IAcquisitionService svc, ValueBus bus, DeviceConfig device, out Pick pick)
    {
        pick = new Pick("", 0);

        var groupOf = new Dictionary<string, GroupConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in device.GroupList)
        {
            if (g.VarList is null) continue;

            foreach (var v in g.VarList)
            {
                if (!string.IsNullOrEmpty(v.VarName)) groupOf[v.VarName] = g;
            }
        }

        int got;

        try
        {
            got = svc.CollectOnce(bus, DateTime.Now);
        }
        catch (Exception ex)
        {
            Console.WriteLine("   采集异常: " + ex.Message);
            return false;
        }

        if (got <= 0)
        {
            Console.WriteLine("   本轮采集 0 个点,原因: " + (svc.LastError ?? "无"));
            return false;
        }

        var snap = bus.GetSnapshot();

        // 优先选设定值里的 Float:它既常量可写、又不会被报警逻辑干扰
        foreach (var kv in snap.Values)
        {
            if (kv.Value.Quality != TagQuality.Good) continue;
            if (!groupOf.TryGetValue(kv.Key, out var g)) continue;
            if (!JogPoints.IsWritableGroup(g)) continue;
            if (!ModbusAcquisitionService.CanWrite(g.Start, FindVar(g, kv.Key), out _)) continue;

            var d = kv.Value.AsDouble();

            if (d is null) continue;

            pick = new Pick(kv.Key, d.Value);
            return true;
        }

        return false;
    }

    private static double? ReadOnce(IAcquisitionService svc, ValueBus bus, string varName)
    {
        try
        {
            svc.CollectOnce(bus, DateTime.Now);
        }
        catch (Exception ex)
        {
            Console.WriteLine("   采集异常: " + ex.Message);
            return null;
        }

        var v = bus.GetValue(varName);

        return v?.Quality == TagQuality.Good ? v.AsDouble() : null;
    }

    private static VariableConfig FindVar(GroupConfig g, string name)
    {
        foreach (var v in g.VarList)
        {
            if (string.Equals(v.VarName, name, StringComparison.OrdinalIgnoreCase)) return v;
        }

        return g.VarList.First();
    }
}