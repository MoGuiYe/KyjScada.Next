namespace KyjScada.Domain.Tests;

/// <summary>
/// "本机没有现场配置,这条用例没法跑"的信号。
///
/// 运行器把它记成 [SKIP] 而不是 PASS —— 环境不满足时假装通过,
/// 换台机器就是"全绿但什么都没验证"。
/// </summary>
public sealed class SkipTestException : Exception
{
    public SkipTestException(string reason) : base(reason)
    {
    }
}