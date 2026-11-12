using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Runtime;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 「切设备 = 整体替换流水线」的回归用例。
///
/// 背景(本轮回归):界面切设备原本是
/// <c>await DropAllAsync(); Build(new[]{device}, null, dropMissing:true);</c> ——
/// DropAllAsync 在 finally 里就把重建锁放掉了,后面的 Build **不在临界区**里。
/// 快速切 A→B 时:
///   · A 的 Build(dropMissing:true) 会把 B 刚建好的流水线**同步 Dispose** 掉
///     (而且绕过了 InFlightWait,不等在途写帧);
///   · B 那边的异步续体随后照样把**已经释放**的对象写进界面的 _pipeline 与 _gate.Writer。
/// 现场表现:"选中的设备不采集、写入打到已关闭的连接"。
///
/// 修法:协调器提供 ReplaceAllAsync(device),释放旧的与建新的**全程持重建锁**。
/// 下面两条用例分别钉住"台账唯一"和"替换不可被打断"。
/// </summary>
public static class CoordinatorReplaceTests
{
    public static void RunAll (Action<string, Action> run)
    {
        run ("切设备:整体替换后台账里只剩目标设备,旧的被真正停掉", () =>
        {
            var coord = MakeCoordinator ();

            try
            {
                var a = coord.ReplaceAllAsync (Dev ("A")).GetAwaiter ().GetResult ();
                var b = coord.ReplaceAllAsync (Dev ("B")).GetAwaiter ().GetResult ();

                TestHelpers.AssertTrue (a is not null && b is not null, "两次替换都应建出流水线");
                TestHelpers.AssertTrue (ReferenceEquals (b, coord.Find ("B")), "台账里的 B 应是本次返回的那条");
                TestHelpers.AssertTrue (coord.Find ("A") is null, "旧设备 A 不能还挂在台账上");
                TestHelpers.AssertEq (1, coord.Pipelines.Count, "台账里只应剩目标设备这一条");

                // 旧流水线必须真的被停掉,否则它还在后台占着串口/连接
                TestHelpers.AssertTrue (!a!.IsRunning, "旧流水线必须已停止");
                TestHelpers.AssertStr ("已停止", a.State, "旧流水线的状态");

                TestHelpers.AssertTrue (b!.IsRunning, "新流水线应已启动");
            }
            finally
            {
                coord.Dispose ();
            }
        });

        run ("切设备:拿不到重建权时必须报 Busy,不能与\"重建失败\"混为一谈", () =>
        {
            var blockA = false;
            var building = new ManualResetEventSlim (false);
            var letGo = new ManualResetEventSlim (false);

            var coord = new AcquisitionCoordinator (device =>
            {
                if (device.DeviceName == "A" && Volatile.Read (ref blockA))
                {
                    building.Set ();
                    letGo.Wait (TimeSpan.FromSeconds (10));
                }

                return new FakeService (device.DeviceName);
            })
            {
                Interval = TimeSpan.FromMilliseconds (50),

                // 等重建权的上限压到 200ms:这条用例要的是"等不到"这个分支
                RebuildTimeout = TimeSpan.FromMilliseconds (200)
            };

            try
            {
                // 正常路径:拿得到重建权 → Replaced
                var ok = coord.RestartAsync (Dev ("A")).GetAwaiter ().GetResult ();

                TestHelpers.AssertTrue (ok.Outcome == RestartOutcome.Replaced
                    && ReferenceEquals (ok.Pipeline, coord.Find ("A")), "正常重建应报 Replaced 并落到台账里");

                // 设备名无效 → 没动手
                var blank = coord.RestartAsync (Dev ("")).GetAwaiter ().GetResult ();

                TestHelpers.AssertTrue (blank.Outcome == RestartOutcome.NotAttempted, "空设备名不该动手");

                // 另一处重建把重建权占住(卡在建线里):RestartAsync 只能等到超时
                Volatile.Write (ref blockA, true);

                var other = Task.Run (() => coord.ReplaceAllAsync (Dev ("A")));

                TestHelpers.AssertTrue (building.Wait (TimeSpan.FromSeconds (5)), "另一处重建应已开始");

                var busy = coord.RestartAsync (Dev ("A")).GetAwaiter ().GetResult ();

                // 这条断言就是 M4 的核心:早先只返回 null,调用方分不清
                // "没轮到(旧流水线还在)"和"重建失败",于是把引用清掉 → 幽灵采集
                TestHelpers.AssertTrue (busy.Outcome == RestartOutcome.Busy,
                    "等不到重建权必须报 Busy,实际 " + busy.Outcome);

                TestHelpers.AssertTrue (!busy.Replaced && busy.Pipeline is null,
                    "报 Busy 时不该给出流水线,也不该声称已换新");

                // 本次调用自己**不能**动台账:里面要么没这条,要么是还在跑的那条
                var found = coord.Find ("A");

                TestHelpers.AssertTrue (found is null || found.IsRunning,
                    "Busy 之后台账里若还有 A,必须是仍在采集的那条");

                letGo.Set ();
                other.GetAwaiter ().GetResult ();

                TestHelpers.AssertTrue (coord.Find ("A") is { IsRunning: true },
                    "重建权放开后 A 应正常落到台账里并在采");
            }
            finally
            {
                letGo.Set ();
                coord.Dispose ();
            }
        });

        run ("切设备:替换进行中不许别的替换插进来(快速连切 A→B 不会互相拆台)", () =>
        {
            var aBuilding = new ManualResetEventSlim (false);
            var letAFinish = new ManualResetEventSlim (false);
            var bBuilding = new ManualResetEventSlim (false);

            // A 的建线过程被卡住:用它把"替换正在进行"这个状态稳定地摆出来
            var coord = new AcquisitionCoordinator (device =>
            {
                if (device.DeviceName == "A")
                {
                    aBuilding.Set ();
                    letAFinish.Wait (TimeSpan.FromSeconds (10));
                }
                else
                {
                    bBuilding.Set ();
                }

                return new FakeService (device.DeviceName);
            });

            try
            {
                var t1 = Task.Run (() => coord.ReplaceAllAsync (Dev ("A")));

                TestHelpers.AssertTrue (aBuilding.Wait (TimeSpan.FromSeconds (5)), "A 的建线应已开始");

                // A 还卡在建线里(重建临界区没放开):此刻发起 B 的替换。
                // 旧实现下 B 会立刻开始建线,于是两条流水线的清空/重建互相交错。
                var t2 = Task.Run (() => coord.ReplaceAllAsync (Dev ("B")));

                Thread.Sleep (300);

                TestHelpers.AssertTrue (!bBuilding.IsSet,
                    "A 的替换还没结束,B 就不该开始建线 —— 释放与重建必须是同一个临界区");

                letAFinish.Set ();

                var pa = t1.GetAwaiter ().GetResult ();
                var pb = t2.GetAwaiter ().GetResult ();

                // 后拿到重建权的那次一定最后生效:台账里只能剩 B,而且就是它返回的实例
                TestHelpers.AssertTrue (pb is not null && ReferenceEquals (pb, coord.Find ("B")),
                    "最后生效的必须是 B,且台账里的 B 就是它返回的那条");

                TestHelpers.AssertTrue (coord.Find ("A") is null, "A 不能还挂在台账上");
                TestHelpers.AssertEq (1, coord.Pipelines.Count, "台账里只应剩 1 条");

                // 被超越的那次:调用方拿到的实例必须已经不在台账里、也已经停了 ——
                // 界面的 Find(name)==pipeline 校验正是靠这一点把过期结果挡掉
                TestHelpers.AssertTrue (pa is null || !ReferenceEquals (pa, coord.Find ("A")),
                    "先返回的 A 不能还挂在台账上");
                TestHelpers.AssertTrue (pa is null || !pa.IsRunning, "被超越的 A 流水线必须已停止");
            }
            finally
            {
                letAFinish.Set ();
                coord.Dispose ();
            }
        });
    }

    private static AcquisitionCoordinator MakeCoordinator () =>
        new (device => new FakeService (device.DeviceName))
        {
            Interval = TimeSpan.FromMilliseconds (50)
        };

    private static DeviceConfig Dev (string name) => new ()
    {
        DeviceName = name,
        ProtocolKey = "ModbusTCP",
        GroupList = new List<GroupConfig> ()
    };

    /// <summary>假采集器:不碰网络,只累加轮次。</summary>
    private sealed class FakeService : IAcquisitionService
    {
        private readonly string _name;

        public FakeService (string name)
        {
            _name = name;
        }

        public int Rounds;

        public string DeviceName => _name;

        public bool IsConnected => true;

        public string? LastError { get; private set; }

        public int CollectOnce (IValueBus bus, DateTime sampleTime)
        {
            Interlocked.Increment (ref Rounds);

            var dict = new Dictionary<string, TagValue> (StringComparer.OrdinalIgnoreCase)
            {
                [_name + "_P0"] = new TagValue (_name + "_P0", 1.0, TagQuality.Good, sampleTime)
            };

            bus.Publish (dict);

            return 1;
        }

        public void Disconnect ()
        {
        }

        public void Dispose ()
        {
        }
    }
}
