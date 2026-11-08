using System.Globalization;
using KyjScada.Mcp.Data;
using KyjScada.Mcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KyjScada.Mcp;

/// <summary>
/// 独立运行模式(用于测试)。
/// 正式集成时,这段托管代码会搬进 WPF 的 App.OnStartup。
///
/// 用法:
///   KyjScada.Mcp.exe --settings "D:\...\settings" --db "D:\...\database\KYJScada" --port 18321
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var settingsDir = GetArg(args, "--settings",
            @"D:\DeepSeekHarness\KyjScada.Next\KyjScada.App\bin\Debug\net8.0-windows\settings");
        var dbPath = GetArg(args, "--db",
            @"D:\DeepSeekHarness\KyjScada.Next\KyjScada.App\bin\Debug\net8.0-windows\data\history.db");
        var port = int.Parse(GetArg(args, "--port", "18321"));

        Console.WriteLine($"settings: {settingsDir}");
        Console.WriteLine($"database: {dbPath}");

        var config = new ConfigReader(settingsDir);
        var dbSelf = new DbReader(dbPath);

        // --selftest:绕过 HTTP 直接调工具方法,打印真实异常(排查 -32603 用)
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            var tools = new ReadOnlyTools(config, dbSelf);
            var writeTools = new WriteTools();
            foreach (var (label, call) in new (string, Func<string>)[]
            {
                ("list_variables", () => tools.ListVariables("LQT")),
                ("list_variables(全部)", () => tools.ListVariables()),
                ("read_variable", () => tools.ReadVariable("LQT_Level")),
                ("read_variables", () => tools.ReadVariables("LQT_Level,KYJ1_OutTemp")),
                ("query_stat", () => tools.QueryStat("LQT_Level",
                    DateTime.Now.AddHours(-2).ToString(DbReader.TimeFormat, CultureInfo.InvariantCulture),
                    DateTime.Now.ToString(DbReader.TimeFormat, CultureInfo.InvariantCulture))),
                ("query_alarms", () => tools.QueryAlarms(
                    DateTime.Now.AddDays(-1).ToString(DbReader.TimeFormat, CultureInfo.InvariantCulture),
                    DateTime.Now.ToString(DbReader.TimeFormat, CultureInfo.InvariantCulture))),
                ("get_device_summary", () => tools.GetDeviceSummary()),
                ("get_write_status", () => writeTools.GetWriteStatus()),
                ("write_variable", () => writeTools.WriteVariable("LQT_Level", 50)),
            })
            {
                try
                {
                    var r = call();
                    Console.WriteLine($"--- {label} OK ---");
                    Console.WriteLine(r.Length > 600 ? r[..600] + "\n...(截断)" : r);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"--- {label} FAIL: {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                }
            }
            return;
        }

        var db = new DbReader(dbPath);

        if (!db.CanOpen())
        {
            Console.WriteLine("WARN: 数据库无法以只读方式打开。历史查询工具将返回错误。");
        }
        else
        {
            var summary = db.GetSummary();
            Console.WriteLine($"ActualData 行数: {summary.ActualDataRows}");
            Console.WriteLine($"数据列数: {summary.DataColumns.Count}");
        }

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(db);

        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<ReadOnlyTools>()
            .WithTools<WriteTools>();

        var app = builder.Build();

        // 必须先 Clear:进程环境里若有 ASPNETCORE_URLS(从服务/脚本继承过来很常见),
        // Kestrel 会**同时**监听那个地址 —— 可能是 0.0.0.0:5000,等于把只读库和
        // (本进程里的)写工具一起放到网络上。Clear 之后只剩下面这一条回环地址。
        app.Urls.Clear();
        app.Urls.Add($"http://127.0.0.1:{port}");
        app.MapMcp("/mcp");

        Console.WriteLine($"MCP server: http://127.0.0.1:{port}/mcp");
        await app.RunAsync();
    }

    private static string GetArg(string[] args, string name, string fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return fallback;
    }
}
