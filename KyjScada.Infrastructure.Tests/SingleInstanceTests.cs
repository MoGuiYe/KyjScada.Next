using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 单实例守卫(上位机不允许重复启动)的回归用例。
///
/// 为什么值得单测而不是"拷到 U 盘上双击两次看看":
///  1. **所有权判定必须用 WaitOne(0)**,不能用 `createdNew`,
///     也不能用"某个文件存在"—— 前者在上一个进程崩溃后仍然返回"已存在",
///     后者在崩溃后永远残留,现场表现是"重启电脑才能再开上位机";
///  2. **Mutex 对持有它的线程是可重入的**:同线程再判一次会"成功",
///     所以"第二个实例"必须在**另一个线程**上试,否则这个用例本身是假的;
///  3. 前一个实例正常退出后必须能再次启动(不能变成一次性)。
/// </summary>
public static class SingleInstanceTests
{
    public static void RunAll(Action<string, Action> run)
    {
        run("单实例:同一安装目录不允许开第二个", () =>
        {
            var id = "inst-" + Guid.NewGuid().ToString("N");

            using var first = SingleInstanceGuard.Acquire(id, out var isFirst1);

            TestHelpers.AssertTrue(isFirst1, "第一个实例必须拿到所有权");
            TestHelpers.AssertTrue(!first.Degraded, "正常环境不应退化成无保护");

            // **必须换线程**:Mutex 是"按线程"可重入的,同线程再 WaitOne 会拿到,
            // 那样测出来的结论是"允许多开",与实际相反。
            var isFirst2 = true;
            var threadId = 0;

            var t = new Thread(() =>
            {
                threadId = Environment.CurrentManagedThreadId;

                using var second = SingleInstanceGuard.Acquire(id, out var got);

                isFirst2 = got;
            });

            t.IsBackground = true;
            t.Start();
            t.Join(TimeSpan.FromSeconds(10));

            TestHelpers.AssertTrue(threadId != 0, "第二个实例的探测线程没有跑起来");
            TestHelpers.AssertTrue(!isFirst2, "第二个实例不能拿到所有权(否则就是允许双开)");
        });

        run("单实例:前一个退出后必须能再次启动", () =>
        {
            var id = "inst-" + Guid.NewGuid().ToString("N");

            var first = SingleInstanceGuard.Acquire(id, out var a);

            TestHelpers.AssertTrue(a, "第一次启动应成功");

            // 模拟关窗退出
            first.Dispose();

            using var again = SingleInstanceGuard.Acquire(id, out var b);

            TestHelpers.AssertTrue(b, "上一个进程退出后必须能再启动,不能变成一次性程序");
        });

        run("单实例:不同安装目录互不阻塞", () =>
        {
            var idA = "instA-" + Guid.NewGuid().ToString("N");
            var idB = "instB-" + Guid.NewGuid().ToString("N");

            using var a = SingleInstanceGuard.Acquire(idA, out var firstA);
            using var b = SingleInstanceGuard.Acquire(idB, out var firstB);

            TestHelpers.AssertTrue(firstA, "A 安装目录应能启动");
            TestHelpers.AssertTrue(firstB,
                "两套独立安装(不同目录/不同现场组态)不能被互相判成多开");
        });

        run("单实例:重复启动要能把已有实例叫起来(有提示入口)", () =>
        {
            var id = "inst-" + Guid.NewGuid().ToString("N");

            using var first = SingleInstanceGuard.Acquire(id, out var isFirst);

            TestHelpers.AssertTrue(isFirst, "第一个实例应拿到所有权");

            using var woke = new ManualResetEventSlim(false);

            first.StartListening(() => woke.Set());

            // 第二个实例(另一个线程上)去叫醒它
            var signaled = false;

            var t = new Thread(() =>
            {
                using var second = SingleInstanceGuard.Acquire(id, out _);

                signaled = second.SignalExistingInstance();
            });

            t.IsBackground = true;
            t.Start();
            t.Join(TimeSpan.FromSeconds(10));

            TestHelpers.AssertTrue(signaled, "应能找到已有实例的通知通道并发出唤醒");
            TestHelpers.AssertTrue(woke.Wait(TimeSpan.FromSeconds(5)),
                "已有实例应收到唤醒(用于把它的窗口切到前台)");
        });
    }
}