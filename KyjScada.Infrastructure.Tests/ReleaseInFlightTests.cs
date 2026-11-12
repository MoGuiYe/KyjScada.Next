using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 「释放必须先等在途写帧」的回归用例。
///
/// 背景(本轮 H1):关窗走 <c>McpHost.StopInBackground()</c>(后台、明确不等在途请求)
/// 之后立刻 <c>_coordinator.Dispose()</c> —— 而 Dispose 直接 p.Dispose(),
/// **完全绕过**了本轮新建的 InFlightWait / WaitForWritesAsync:
/// MCP 在途写帧还没发完,Service.Dispose() 就把串口句柄关了 →
/// 写结果不明、串口异常;同时 StopAndWait(2s) 在界面线程同步阻塞,关窗最多卡 2 秒。
///
/// 修法(本文件钉住的两条性质):
///   1. DevicePipeline.Dispose() 自己就等在途写 —— 谁调 Dispose 都绕不过去,
///      不必指望调用方记得挑 ReleaseAsync;
///   2. AcquisitionCoordinator.ReleaseAllAsync() 把释放挪离调用线程 ——
///      关窗路径可以在界面线程上一行 await 不掉地交给它,界面不再被拖住。
/// </summary>
public static class ReleaseInFlightTests
{
    public static void RunAll (Action<string, Action> run)
    {
        run ("释放:同步 Dispose 也必须等在途写帧收尾,不能直接关句柄", () =>
        {
            var svc = new CountingService ("D");

            // 模拟命令闸门里还有一帧下到一半:钩子返回 Task,写完了才 Set

            var waits = 0;
            var settling = new TaskCompletionSource<bool> ();

            var pipeline = new DevicePipeline (Dev ("D"), new ValueBus (), svc)
            {
                Interval = TimeSpan.FromMilliseconds (50),

                // 释放方必须等它结束
                InFlightWait = timeout =>
                {
                    Interlocked.Increment (ref waits);

                    return settling.Task;
                }
            };

            pipeline.Start ();

            // 在后台线程释放:等待是真的,测试线程不能被它挂住
            var release = Task.Run (pipeline.Dispose);

            // 释放卡在"等在途写"上:这期间**不能**已经关掉连接
            TestHelpers.AssertTrue (!release.Wait (TimeSpan.FromMilliseconds (200)),
                "在途写未结束时 Dispose 不该已经返回");

            TestHelpers.AssertEq (0, svc.DisposedCount,
                "在途写未结束时不该关掉采集器的连接(句柄还在被那一帧用着)");

            // 那一帧写完了:释放随后完成,连接才关
            settling.TrySetResult (true);

            TestHelpers.AssertTrue (release.Wait (TimeSpan.FromSeconds (10)),
                "在途写结束后释放应正常返回");

            TestHelpers.AssertTrue (Volatile.Read (ref waits) >= 1,
                "Dispose 必须问过在途写(不能绕过 InFlightWait)");

            TestHelpers.AssertEq (1, svc.DisposedCount, "连接应被关掉且只关一次");

            TestHelpers.AssertTrue (!pipeline.IsRunning, "释放后采集循环应已停止");
        });

        run ("释放:重复 Dispose 不重复等在途写(关窗可能走两遍)", () =>
        {
            var svc = new CountingService ("D");


            var waits = 0;

            var pipeline = new DevicePipeline (Dev ("D"), new ValueBus (), svc)
            {
                Interval = TimeSpan.FromMilliseconds (50),
                InFlightWait = _ =>
                {
                    Interlocked.Increment (ref waits);

                    return Task.FromResult (true);
                }
            };

            pipeline.Start ();

            pipeline.Dispose ();

            var second = System.Diagnostics.Stopwatch.StartNew ();

            pipeline.Dispose ();
            pipeline.ReleaseAsync ().GetAwaiter ().GetResult ();

            second.Stop ();

            TestHelpers.AssertEq (1, Volatile.Read (ref waits), "在途等待只该发生一次");
            TestHelpers.AssertEq (1, svc.DisposedCount, "连接只该关一次");
            TestHelpers.AssertTrue (second.ElapsedMilliseconds < 500,
                "第二次释放应立刻返回,实际 " + second.ElapsedMilliseconds + " ms");
        });

        run ("关窗:ReleaseAllAsync 在调用线程上不等,且在途写等待有上界", () =>
        {
            // 在途写永远不结束(= 写入卡死):释放仍必须在有界时间内完成,
            // 不能把关窗挂死在一个永不返回的钩子上。
            var never = new TaskCompletionSource<bool> ();

            var coord = new AcquisitionCoordinator (d => new CountingService (d.DeviceName))
            {
                Interval = TimeSpan.FromMilliseconds (50),
                BeforeRelease = _ => never.Task
            };

            try
            {
                var p = coord.ReplaceAllAsync (Dev ("A")).GetAwaiter ().GetResult ();

                TestHelpers.AssertTrue (p is { IsRunning: true }, "应先建好并跑起一条流水线");
                TestHelpers.AssertEq (1, coord.Pipelines.Count, "台账里应有 1 条");

                var sw = System.Diagnostics.Stopwatch.StartNew ();

                var releaseAll = coord.ReleaseAllAsync ();

                sw.Stop ();

                // 关键 1:调用线程(关窗时的界面线程)立刻拿回控制权,
                // 不会因为 StopAndWait 或 InFlightWait 被同步占住。
                TestHelpers.AssertTrue (sw.ElapsedMilliseconds < 200,
                    "ReleaseAllAsync 不应阻塞调用线程,实际 " + sw.ElapsedMilliseconds + " ms");

                // 关键 2:台账同步清空 —— 界面不会再往已释放的流水线上挂写入
                TestHelpers.AssertEq (0, coord.Pipelines.Count, "台账应立刻清空");

                var bounded = System.Diagnostics.Stopwatch.StartNew ();
                var finished = releaseAll.Wait (TimeSpan.FromSeconds (30));
                bounded.Stop ();

                TestHelpers.AssertTrue (finished,
                    "在途写卡死时释放也必须在上界内完成(原来会无限等)");

                // 上界 = InFlightWaitTimeout(3s) + StopAndWait(2s),留足调度余量
                var limit = DevicePipeline.InFlightWaitTimeout.TotalSeconds * 1000 + 2000 + 5000;

                TestHelpers.AssertTrue (bounded.ElapsedMilliseconds < limit,
                    "释放耗时应受在途等待与停止等待的上界约束,实际 " + bounded.ElapsedMilliseconds
                    + " ms(上界 " + limit + " ms)");

                TestHelpers.AssertTrue (p is not null && !p.IsRunning, "释放后流水线应已停止");
            }
            finally
            {
                never.TrySetResult (true);

                coord.Dispose ();
            }
        });
    }

    private static DeviceConfig Dev (string name) => new ()
    {
        DeviceName = name,
        ProtocolKey = "ModbusTCP",
        GroupList = new List<GroupConfig> ()
    };

    /// <summary>假采集器:不碰网络,只记轮次与"被关闭了几次"。</summary>
    private sealed class CountingService : IAcquisitionService
    {
        private readonly string _name;

        private int _disposed;

        public CountingService (string name)
        {
            _name = name;
        }

        public int Rounds;

        /// <summary>连接被关掉的次数。</summary>
        public int DisposedCount => Volatile.Read (ref _disposed);

        public string DeviceName => _name;

        public bool IsConnected => true;

        public string? LastError { get; private set; }

        public int CollectOnce (IValueBus bus, DateTime sampleTime)
        {
            Interlocked.Increment (ref Rounds);

            return 1;
        }

        public void Disconnect ()
        {
        }

        public void Dispose ()
        {
            Interlocked.Increment (ref _disposed);
        }
    }
}
