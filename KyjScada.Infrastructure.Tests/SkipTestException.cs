namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// "本机没法跑这条用例"的信号(缺现场配置、设备没连通等)。
///
/// 原来这类用例写的是 <c>if (path is null) return;</c> —— 环境不满足时直接返回,
/// 运行器把它记成 PASS。于是 CI/别的机器上"通过 306"里混着从来没执行过的用例,
/// 报告看着全绿,实际上什么都没验证。现在改成抛这个异常:
/// 运行器单独记 [SKIP] 并计数,既不冒充通过,也不误报失败。
/// </summary>
public sealed class SkipTestException : Exception
{
    public SkipTestException(string reason) : base(reason)
    {
    }
}