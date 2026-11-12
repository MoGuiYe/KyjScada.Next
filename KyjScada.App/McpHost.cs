using KyjScada.Mcp.Data;
using KyjScada.Mcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
    /// <summary>Start/Stop 的串行锁。没有它时界面线程停、后台线程起会同时改 _app/WriteHandler。</summary>
    private static readonly object _gate = new();

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
        lock (_gate)
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

            // app 在 try 外声明:启动失败时 catch 里必须还能拿到它去做 Stop/Dispose。
            // 原来 app 是 try 内的局部变量,catch 只能把 _app 置 null —— 已经起来的监听没人关,
            // 端口一直占着,RestartMcp 用同一个端口必然再次失败(现场表现:改了设置重启就再也起不来)。
            WebApplication? app = null;

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

                app = builder.Build();

                app.Urls.Clear();
                app.Urls.Add("http://127.0.0.1:" + port);

                // 绑定 127.0.0.1 只挡"从别的机器连",挡不住"本机浏览器里的恶意页面":
                // 浏览器会给 attacker.com 解析到 127.0.0.1 再发请求(DNS rebinding),
                // Host 头里带的是 attacker.com。所以再加一道 Host/Origin 白名单 + token。
                //
                // token 默认**必须**:没配环境变量就自动生成一个并落盘,
                // 否则本机任意进程都能借着"闸门已开启"写 PLC(见 IsAllowedRequest 的注释)。
                var token = ResolveToken();

                app.Use(async (ctx, next) =>
                {
                    if (!IsAllowedRequest(ctx, port, token))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;

                        await ctx.Response.WriteAsync("forbidden");

                        return;
                    }

                    await next();
                });

                app.MapMcp("/mcp");

                // StartAsync 之后请求就开始进来了;不 await 到结束(那要等进程退出)。
                // 关键:必须在后台线程上等 —— ASP.NET 主机会把一部分延续投递到"调用 StartAsync 的线程"
                // 的同步上下文。若在 WPF 界面线程上同步等待,那一半延续永远排不进消息队列,
                // 后果是 StopAsync 永不返回、窗口卡死(实测过)。Task.Run 里没有 SynchronizationContext,安全。
                var startTask = Task.Run(() => app.StartAsync());
                var completed = false;

                try
                {
                    completed = startTask.Wait(TimeSpan.FromSeconds(10));
                }
                catch (AggregateException ex)
                {
                    // Wait 在任务失败时直接抛,原来那句 IsFaulted 检查因此永远走不到。
                    // 这里取出真正的异常再交给外层;外层会 Dispose 这个 app。
                    throw ex.GetBaseException();
                }

                if (!completed) throw new TimeoutException("10 秒内没起来(端口被占用或防火墙拦截?)");

                // 已完成:GetResult 只会把真正的失败原因抛出来,不会包装成 AggregateException
                startTask.GetAwaiter().GetResult();

                _app = app;
                Endpoint = "http://127.0.0.1:" + port + "/mcp";
                Status = (writeHandler is null ? "已启动 " + Endpoint + "(写通道未启用)" : "已启动 " + Endpoint + "(含写入)")
                    + (string.IsNullOrEmpty(token) ? "" : "(需 token)");

                DiagLog("Start OK -> " + Endpoint);
            }
            catch (Exception ex)
            {
                _app = null;
                Endpoint = "";
                WriteTools.WriteHandler = null;
                Status = "启动失败:" + ex.Message;

                // 半启动的主机也要关掉:StartAsync 可能已经绑上端口再抛(中间件/工具注册失败),
                // 不 Stop/Dispose 就是孤儿监听,占着端口直到进程退出。
                //
                // 这里必须 **wait:true** 同步等关停完成:失败之后用户第一反应就是“重启 MCP”,
                // 而 RestartMcp 走的是同一个端口 —— fire-and-forget 的关停还没把端口放掉,
                // 重启必然再失败一次(现场表现:点一次失败,再点永远失败)。
                // 启动失败路径本来就要给用户一句明确结论,多等这几秒是可以接受的代价。
                if (app is not null)
                {
                    try
                    {
                        DisposeApp(app, wait: true);
                    }
                    catch
                    {
                        // 失败路径上不能再抛
                    }
                }

                DiagLog("Start FAIL: " + ex, force: true);
            }
        }
    }

    /// <summary>自动生成的 token 落盘位置(供本机客户端读取)。</summary>
    public static string TokenFile =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "data", "mcp_token.txt");

    /// <summary>
    /// 取本次运行使用的 token。
    ///
    /// 优先环境变量 KYJ_MCP_TOKEN;没配就生成一个随机值并写入 <see cref="TokenFile"/> ——
    /// 客户端(本机 AI 工具)读这个文件拿 token,在请求头里带
    /// `Authorization: Bearer &lt;token&gt;`。
    /// 显式设 KYJ_MCP_ALLOW_ANONYMOUS=1 可退回"不校验"(仅用于老客户端改不动的场合)。
    ///
    /// **落盘位置只认 TokenFile 一处。** 早先这里又按 BaseDirectory\data 硬拼了一遍
    /// 同一个路径,而 TokenFile 是**给客户端读**的那份声明 ——
    /// 改一处漏一处就是"程序写 A、客户端读 B",现场表现是
    /// "token 明明在文件里,请求却一直 403",而且看代码两处都像是对的。
    /// </summary>
    private static string ResolveToken()
    {
        return KyjScada.Mcp.McpAccessGuard.ResolveToken(TokenFile);
    }

    /// <summary>
    /// 请求准入:只接受"从本机回环地址、用本机主机名、来源是本机页面、且带正确 token"的请求。
    ///
    /// 实现与独立版(KyjScada.Mcp 的 Program)共用 <see cref="KyjScada.Mcp.McpAccessGuard"/>:
    /// 两处各写一遍时,独立版曾经完全没有这道闸。
    /// </summary>
    private static bool IsAllowedRequest(HttpContext ctx, int port, string? token)
    {
        return KyjScada.Mcp.McpAccessGuard.IsAllowedRequest(ctx, port, token);
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

    /// <summary>
    /// 主程序退出时调用(同步,最多卡 6 秒)。
    /// 只在"确实要等它停干净"的地方用 —— 关窗路径请用 <see cref="StopInBackground"/>。
    /// </summary>
    public static void Stop()
    {
        var app = Detach();

        if (app is not null) DisposeApp(app, wait: true);
    }

    /// <summary>
    /// 关窗路径专用:不阻塞界面线程。
    ///
    /// 原来这里同步 Stop,而 MCP 的写工具会 Dispatcher.Invoke 回界面线程:
    /// 界面线程等 StopAsync,StopAsync 的在途请求等界面线程 —— 关窗时偶发卡死几十秒。
    /// 状态(Endpoint/WriteHandler)仍然同步清掉,所以关窗后不会再有新的写请求进来。
    /// </summary>
    public static void StopInBackground()
    {
        // Detach 也要放到**线程池**:它取的是 _gate,而 Start 正在启动时会一直握着它
        // (最长 10 秒 —— 等 StartAsync)。若在界面线程上调 Detach,关窗就会冻结这 10 秒,
        // 现场表现是"点关闭,窗口十几秒没反应"。
        // 状态清理仍然是同步语义上的"很快"(锁一空出来就清),只是不占界面线程等。
        Task.Run(() =>
        {
            var app = Detach();

            if (app is null) return;

            DisposeApp(app, wait: false);
        });
    }

    /// <summary>摘掉当前实例并同步清空对外状态;返回要释放的实例。</summary>
    private static WebApplication? Detach()
    {
        lock (_gate)
        {
            var app = _app;

            DiagLog("Stop() 当前 _app=" + (app is null ? "null" : "running"));

            _app = null;
            Endpoint = "";
            WriteTools.WriteHandler = null;
            Status = "已停止";

            return app;
        }
    }

    private static void DisposeApp(WebApplication app, bool wait)
    {
        if (!wait)
        {
            // 不等人:交给线程池,进程随后退出时端口由操作系统回收
            Task.Run(async () =>
            {
                try
                {
                    await app.StopAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 退出路径上不抛
                }

                try
                {
                    await app.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 同上
                }
            });

            return;
        }

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