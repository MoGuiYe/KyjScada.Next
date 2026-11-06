using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Domain.Protocol;
using KyjScada.Infrastructure.Acquisition;
using KyjScada.Infrastructure.Modbus;
using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

using static KyjScada.Infrastructure.Tests.TestHelpers;

/// <summary>
/// 断线行为回归(现场反馈"通讯没连上时界面卡顿"的代码侧防护)。
///
/// 三条硬约束,任何一条破了都会在现场变成"界面看着像死了":
///  1. 连不上只能是 <see cref="ModbusException"/>,**不许漏出 SocketException / IOException**
///     —— 漏出去采集器就跳过 Publish,界面会一直显示断线前的旧值。
///  2. 连不上必须把全部变量标成 NotCollected。
///  3. 刚失败过的连接要"快速失败"(冷静期内不再真的去连),
///     否则界面线程上的一次写操作就要等一整个超时。
/// </summary>
public static class TransportFailureTests
{
    public static void RunAll (Action<string, Action> run)
    {
        run ("断线保护:RTU-over-TCP 连不上不抛裸异常,变量标未采集", () =>
        {
            var port = FreeTcpPort ();
            var svc = BuildService (Device (new ConnectionConfig
            {
                Kind = ConnectionKind.Tcp, IpAddress = "127.0.0.1", Port = port
            }, "ModbusRTUOverTCP"), "ModbusRTUOverTCP");

            var bus = new ValueBus ();

            try
            {
                svc.CollectOnce (bus, DateTime.Now); // 不许抛

                var tv = bus.GetSnapshot ()["T1"];

                AssertTrue (tv is not null, "总线上应有 T1");
                AssertTrue (tv.Value is null, "断线时值必须为 null,不能沿用旧值");
                AssertEq ((int)TagQuality.NotCollected, (int)tv.Quality, "断线时质量必须是 NotCollected");
                AssertTrue (!string.IsNullOrWhiteSpace (svc.LastError), "必须记下断线原因");
                AssertTrue (!svc.IsConnected, "断线后不能自称已连接");
            }
            finally { svc.Dispose (); }
        });

        run ("断线保护:ModbusTCP 连不上不抛裸异常,变量标未采集", () =>
        {
            var port = FreeTcpPort ();
            var svc = BuildService (Device (new ConnectionConfig
            {
                Kind = ConnectionKind.Tcp, IpAddress = "127.0.0.1", Port = port
            }, "ModbusTCP"), "ModbusTCP");

            var bus = new ValueBus ();

            try
            {
                svc.CollectOnce (bus, DateTime.Now);

                var tv = bus.GetSnapshot ()["T1"];

                AssertTrue (tv.Value is null, "断线时值必须为 null");
                AssertEq ((int)TagQuality.NotCollected, (int)tv.Quality, "断线时质量必须是 NotCollected");
                AssertTrue (!string.IsNullOrWhiteSpace (svc.LastError), "必须记下断线原因");
            }
            finally { svc.Dispose (); }
        });

        run ("断线保护:串口打不开不抛裸异常,变量标未采集", () =>
        {
            var svc = BuildService (Device (new ConnectionConfig
            {
                Kind = ConnectionKind.Serial, PortName = "COM_NO_SUCH_PORT", BaudRate = 9600
            }, "ModbusRTU"), "ModbusRTU");

            var bus = new ValueBus ();

            try
            {
                svc.CollectOnce (bus, DateTime.Now);

                var tv = bus.GetSnapshot ()["T1"];

                AssertTrue (tv.Value is null, "串口打不开时值必须为 null");
                AssertEq ((int)TagQuality.NotCollected, (int)tv.Quality, "质量必须是 NotCollected");
                AssertTrue (!string.IsNullOrWhiteSpace (svc.LastError), "必须记下串口打不开的原因");
            }
            finally { svc.Dispose (); }
        });

        run ("断线保护:连接失败后立刻重试走「快速失败」,不再等一整个超时", () =>
        {
            // 192.0.2.0/24 是 TEST-NET-1,保证不可达:真实 TCP 会一直重试 SYN
            var transport = new ModbusRtuOverTcpTransport ("192.0.2.1", 502)
            {
                Timeout = TimeSpan.FromMilliseconds (400),
                ConnectBackoff = TimeSpan.FromSeconds (5)
            };

            try
            {
                var first = Time (() => Try (() => transport.Connect ()));
                var second = Time (() => Try (() => transport.Connect ()));

                Console.WriteLine ("         首次连接 " + first + " ms,冷静期内重试 " + second + " ms");

                AssertTrue (second < 100, "冷静期内重试必须立即失败,实际 " + second + " ms");
                AssertTrue (first >= 300, "首次连接应真的等超时(否则测试环境不可达性没生效),实际 " + first + " ms");
            }
            finally { transport.Dispose (); }
        });

        run ("断线保护:连不上时写入立即失败(界面线程不再被拖住)", () =>
        {
            var transport = new ModbusRtuOverTcpTransport ("192.0.2.1", 502)
            {
                Timeout = TimeSpan.FromMilliseconds (400),
                ConnectBackoff = TimeSpan.FromSeconds (5)
            };

            try
            {
                Try (() => transport.ReadHoldingRegisters (0, 1)); // 先把失败记上

                var ms = Time (() => Try (() => transport.WriteRegister (0, 1)));

                Console.WriteLine ("         断线状态写寄存器耗时 " + ms + " ms");

                AssertTrue (ms < 100, "断线时写入必须立即失败,实际 " + ms + " ms");
            }
            finally { transport.Dispose (); }
        });
    }

    private static DeviceConfig Device (ConnectionConfig conn, string protocolKey)
    {
        var device = new DeviceConfig
        {
            DeviceName = "D1",
            ProtocolKey = protocolKey,
            WordOrder = WordOrder.Abcd,
            Connection = conn
        };

        var group = new GroupConfig { GroupName = "G1", IsActive = true, Start = "1.400001", Length = 10 };

        group.VarList.Add (new VariableConfig
        {
            VarName = "T1",
            VarType = "Int16",
            VarAddress = "1.400001",
            Start = "1.400001"
        });

        device.GroupList.Add (group);

        return device;
    }

    private static ModbusAcquisitionService BuildService (DeviceConfig device, string protocolKey)
    {
        return new ModbusAcquisitionService (device, ProtocolRegistry.Find (protocolKey));
    }

    /// <summary>拿一个"当下没人监听"的本机端口:绑一下再放掉,端口就是关闭状态。</summary>
    private static int FreeTcpPort ()
    {
        var listener = new System.Net.Sockets.TcpListener (System.Net.IPAddress.Loopback, 0);

        listener.Start ();

        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        listener.Stop ();

        return port;
    }

    private static void Try (Action action)
    {
        try { action (); }
        catch (ModbusException) { }
    }

    private static long Time (Action action)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew ();

        action ();

        return sw.ElapsedMilliseconds;
    }
}