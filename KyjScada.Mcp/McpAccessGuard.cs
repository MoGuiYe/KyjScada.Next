using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace KyjScada.Mcp;

/// <summary>
/// MCP HTTP 入口的准入判定。**内嵌版(App/McpHost)与独立版(Program)共用这一份**。
///
/// 为什么必须共用:两处曾经各写一遍,内嵌版有 Host/Origin 白名单 + token,
/// 而独立版什么都没挂 —— 同一个"只读库 + 写工具"的服务,换个启动方式就裸奔。
/// 这类"改一处漏一处"的漏洞看代码时两边都像是对的,只能靠单一实现避免。
///
/// 三层:
///  1. 只监听 127.0.0.1(调用方负责);
///  2. Host/Origin 白名单 —— 挡浏览器 DNS rebinding(attacker.com 解析到 127.0.0.1);
///  3. token —— 挡**本机任意进程**。第 2 层挡不住这个:任何本地程序 curl 一下
///     就能在闸门开启时写 PLC,而闸门是操作员为正常作业打开的,不会为此关掉。
/// </summary>
public static class McpAccessGuard
{
    /// <summary>
    /// 取本次运行使用的 token。
    ///
    /// 优先环境变量 KYJ_MCP_TOKEN;没配就生成一个随机值并落盘 ——
    /// 客户端(本机 AI 工具)读这个文件拿 token,请求头带 `Authorization: Bearer &lt;token&gt;`。
    /// 显式设 KYJ_MCP_ALLOW_ANONYMOUS=1 可退回"不校验"(仅用于老客户端改不动的场合)。
    /// </summary>
    /// <param name="tokenFile">token 落盘路径。**只认这一处**:调用方与客户端读同一个文件。</param>
    public static string ResolveToken(string tokenFile)
    {
        var configured = Environment.GetEnvironmentVariable("KYJ_MCP_TOKEN");

        if (!string.IsNullOrEmpty(configured)) return configured;

        if (Environment.GetEnvironmentVariable("KYJ_MCP_ALLOW_ANONYMOUS") == "1") return "";

        try
        {
            var dir = Path.GetDirectoryName(tokenFile);

            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 已有就复用:重启后客户端不必重新配置
            if (File.Exists(tokenFile))
            {
                var existing = File.ReadAllText(tokenFile).Trim();

                if (existing.Length >= 16) return existing;
            }

            var fresh = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

            File.WriteAllText(tokenFile, fresh, new System.Text.UTF8Encoding(false));

            return fresh;
        }
        catch
        {
            // 生成失败时**拒绝所有请求**比放行安全:放行等于任意本地进程可写 PLC
            return "unavailable-" + Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>
    /// 请求准入:只接受"从本机回环地址、用本机主机名、来源是本机页面、且带正确 token"的请求。
    /// </summary>
    public static bool IsAllowedRequest(HttpContext ctx, int port, string? token)
    {
        var host = ctx.Request.Host;

        var name = host.Host ?? "";

        var localHost = name.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("::1", StringComparison.OrdinalIgnoreCase)
            || name.Equals("[::1]", StringComparison.OrdinalIgnoreCase);

        if (!localHost) return false;

        if (host.Port.HasValue && host.Port.Value != port) return false;

        // 浏览器发跨站请求一定带 Origin;不是本机来源就是有人在借用你这台机器的写权限
        var origin = ctx.Request.Headers["Origin"].ToString();

        if (!string.IsNullOrEmpty(origin))
        {
            var ok = origin.Equals("http://127.0.0.1:" + port, StringComparison.OrdinalIgnoreCase)
                || origin.Equals("http://localhost:" + port, StringComparison.OrdinalIgnoreCase);

            if (!ok) return false;
        }

        if (string.IsNullOrEmpty(token))
        {
            return Environment.GetEnvironmentVariable("KYJ_MCP_ALLOW_ANONYMOUS") == "1";
        }

        return FixedTimeEqualsOne(
            ctx.Request.Headers["Authorization"].ToString(),
            "Bearer " + token);
    }

    /// <summary>
    /// 挂上准入中间件。调用方必须先 Clear Urls 并只 Add 回环地址。
    /// </summary>
    public static void Use(WebApplication app, int port, string? token)
    {
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
    }

    /// <summary>
    /// token 比较必须是恒定时间。用 == 比较字符串时,运行库会在第一个不同字符处返回,
    /// 比较耗时随"前多少位猜对"变化 —— 攻击者能靠计时把 token 一位一位试出来。
    ///
    /// 长度不同时也先补齐再比,避免"长度不对立刻返回"同样泄露长度信息;
    /// 长度差异本身由返回值体现,不影响结果正确性。
    /// </summary>
    private static bool FixedTimeEqualsOne(string? actual, string expected)
    {
        actual ??= "";

        var a = System.Text.Encoding.UTF8.GetBytes(actual);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);

        var len = Math.Max(a.Length, b.Length);

        var pa = new byte[len];
        var pb = new byte[len];

        Array.Copy(a, pa, a.Length);
        Array.Copy(b, pb, b.Length);

        // 两个条件都要算完再与,不能短路 —— 短路会把长度差异泄露成时间差
        var same = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(pa, pb);

        return same && a.Length == b.Length;
    }
}