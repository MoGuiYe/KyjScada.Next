using KyjScada.Mcp.Data;
using KyjScada.Mcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KyjScada.App;

/// <summary>
/// 把 MCP 服务嵌进主程序进程。
///
/// 为什么不另起一个 KyjScada.Mcp.exe:
///  1. 读工具要读"当前这台机器的组态与库",两个进程各自解析会读到不同的文件(甚至半截写的配置)
///  2. 写工具必须走主程序的 CommandGate —— 那里才有权限判定、最小写入间隔、审计记录;
///     独立进程拿不到这些,只能绕过闸门直接写设备,是最危险的做法
///  3. 现场只允许一个进程持有通讯通道,双进程会抢端口
///
/// 因此:主程序启动时起 HTTP MCP,把写回调注入 WriteTools;主程序退出时关掉。
/// 端口来自 app_settings.ini 的 McpPort(0 = 不启动)。
/// </summary>
internal static class McpHost
{
    private static WebApplication? _app;

    /// <summary>监听地址;未启动时为空串。</summary>
    public static string Endpoint { get; private set; } = "";

    /// <summary>给人看的状态,显示在状态栏上。</summary>
    public static string Status { get; private set; } = "未启动";

    public static bool Running => _app is not null;

    /// <summary>
    /// 启动内嵌 MCP。任何异常都只记录在 <see cref="Status"/> 里,
    /// 绝不能让监控主程序因为 MCP 起不来而崩掉。
    /// </summary>
    /// <param name="settingsDir">组态目录(settings)</param>
    /// <param name="dbPath">历史库(history.db)</param>
    /// <param name="port">监听端口,0 = 不启动</param>
    /// <param name="writeHandler">写回调:(变量名, 值) → 结果文本,由主程序转成 CommandGate 调用</param>
    public static void Start(string settingsDir, string dbPath, int port, Func<string, double, string>? writeHandler)
    {
        DiagLog("Start(port=" + port + ") 当前 _app=" + (_app is null ? "null" : "running"));

        if (port <= 0)
        {
            Status = "未启用(McpPort=0)";
            return;
        }

        if (_app is not null)
        {
            Status = "已在运行 " + Endpoint;
            return;
        }

        try
        {
            WriteTools.WriteHandler = writeHandler;

            var builder = WebApplication.CreateBuilder();

            builder.Logging.ClearProviders();

            // 两个进程共用同一个组态/库 —— 只读打开,不会干扰主程序的写入
            builder.Services.AddSingleton(new ConfigReader(settingsDir));
            builder.Services.AddSingleton(new DbReader(dbPath));

            builder.Services.AddMcpServer()
                .WithHttpTransport()
                .WithTools<ReadOnlyTools>()
                .WithTools<WriteTools>();

            var app = builder.Build();

            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:" + port);
            app.MapMcp("/mcp");

            // StartAsync 之后请求就开始进来了;不 await 到结束(那要等进程退出)。
            // 关键:必须在后台线程上等 —— ASP.NET 主机会把一部分延续投递到"调用 StartAsync 的线程"
            // 的同步上下文。若在 WPF 界面线程上同步等待,那一半延续永远排不进消息队列,
            // 后果是 StopAsync 永不返回、窗口卡死(实测过)。Task.Run 里没有 SynchronizationContext,安全。
            var startTask = Task.Run(() => app.StartAsync());

            if (!startTask.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("10 秒内没起来(端口被占用或防火墙拦截?)");
            }

            if (startTask.IsFaulted && startTask.Exception is not null)
            {
                throw startTask.Exception.GetBaseException();
            }

            _app = app;
            Endpoint = "http://127.0.0.1:" + port + "/mcp";
            Status = writeHandler is null
                ? "已启动 " + Endpoint + "(写通道未启用)"
                : "已启动 " + Endpoint + "(含写入)";

            DiagLog("Start OK -> " + Endpoint);
        }
        catch (Exception ex)
        {
            _app = null;
            Endpoint = "";
            WriteTools.WriteHandler = null;
            Status = "启动失败:" + ex.Message;

            DiagLog("Start FAIL: " + ex, force: true);
        }
    }

    /// <summary>
    /// 排查用日志:失败一律记;正常启停只在设了环境变量 KYJ_MCP_DIAG=1 时才记,
    /// 免得平时在 data 目录里留一堆没人看的文件。
    /// </summary>
    private static void DiagLog(string message, bool force = false)
    {
        if (!force
            && Environment.GetEnvironmentVariable("KYJ_MCP_DIAG") != "1")
        {
            return;
        }

        try
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "data");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "mcp_debug.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
        }
        catch
        {
            // 诊断日志本身绝不能影响业务
        }
    }

    /// <summary>主程序退出时调用。</summary>
    public static void Stop()
    {
        var app = _app;

        DiagLog("Stop() 当前 _app=" + (app is null ? "null" : "running"));

        _app = null;
        Endpoint = "";
        WriteTools.WriteHandler = null;
        Status = "已停止";

        if (app is null) return;

        try
        {
            // 同 Start:后台线程上停,避免延续卡在界面线程的同步上下文里
            var stopTask = Task.Run(() => app.StopAsync());

            if (!stopTask.Wait(TimeSpan.FromSeconds(3)))
            {
                DiagLog("StopAsync 3 秒没停完,直接 Dispose 释放端口", force: true);
            }
        }
        catch (Exception ex)
        {
            // 退出路径上不抛异常:超时/取消都继续往下 Dispose
            DiagLog("StopAsync 异常: " + ex.Message, force: true);
        }

        try
        {
            var disposeTask = Task.Run(() => app.DisposeAsync().AsTask());

            if (!disposeTask.Wait(TimeSpan.FromSeconds(3)))
            {
                DiagLog("DisposeAsync 3 秒没完(端口可能还占着)", force: true);
            }

            DiagLog("Stop 完成");
        }
        catch (Exception ex)
        {
            DiagLog("DisposeAsync 异常: " + ex.Message, force: true);
        }
    }
}