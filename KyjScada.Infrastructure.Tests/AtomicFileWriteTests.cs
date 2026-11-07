using KyjScada.Infrastructure.Export;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// 导出落盘收尾测试。
///
/// 重点不是"能写文件",而是"出事的时候原文件还在" ——
/// 现场最典型的场景就是上一次导出的文件还开在 Excel 里(被独占句柄锁住),
/// 用户再导一次,旧代码会先 Delete 再 Move,Move 一旦失败原文件就没了。
/// </summary>
public static class AtomicFileWriteTests
{
    private static string Dir() =>
        Path.Combine(AppContext.BaseDirectory, "_work", "atomic");

    private static string PathOf(string name)
    {
        var dir = Dir();
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        return Path.Combine(dir, name);
    }

    public static void RunAll(Action<string, Action> run)
    {
        run("原子写:目标不存在时直接落盘", () =>
        {
            var target = PathOf("new.csv");
            var tmp = target + ".tmp";

            if (File.Exists(target)) File.Delete(target);
            File.WriteAllText(tmp, "hello");

            AtomicFileWrite.Replace(tmp, target);

            TestHelpers.AssertStr("hello", File.ReadAllText(target), "内容应写入目标");
            TestHelpers.AssertTrue(!File.Exists(tmp), "临时文件应已被移走");
        });

        run("原子写:目标已存在时被覆盖", () =>
        {
            var target = PathOf("exists.csv");
            var tmp = target + ".tmp";

            File.WriteAllText(target, "old");
            File.WriteAllText(tmp, "new");

            AtomicFileWrite.Replace(tmp, target);

            TestHelpers.AssertStr("new", File.ReadAllText(target), "应覆盖成新内容");
            TestHelpers.AssertTrue(!File.Exists(tmp), "临时文件应已被替换掉");
            TestHelpers.AssertTrue(!File.Exists(target + ".old"), "不应残留 .old 文件");
        });

        run("原子写:目标被独占时保留原文件并清掉临时文件", () =>
        {
            var target = PathOf("locked.csv");
            var tmp = target + ".tmp";

            File.WriteAllText(target, "old");
            File.WriteAllText(tmp, "new");

            // 用独占句柄模拟"文件正开在 Excel 里"。
            // 三种替换方式(Replace / 覆盖式 Move / 挪开再 Move)都会失败,
            // 此时唯一正确的行为是:抛出异常 + 原文件内容不变 + 不留下 .tmp。
            var writeFailed = false;

            using (new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    AtomicFileWrite.Replace(tmp, target);
                }
                catch
                {
                    writeFailed = true;
                }
            }

            TestHelpers.AssertTrue(writeFailed, "被独占时应抛异常让界面提示");
            TestHelpers.AssertStr("old", File.ReadAllText(target), "原文件内容不能变");
            TestHelpers.AssertTrue(!File.Exists(tmp), "临时文件应被清掉");
            TestHelpers.AssertTrue(!File.Exists(target + ".old"), "不应残留 .old 文件");
        });

        run("原子写:临时文件不存在时抛异常且不动目标", () =>
        {
            var target = PathOf("notmp.csv");

            File.WriteAllText(target, "keep");

            var threw = false;

            try
            {
                AtomicFileWrite.Replace(target + ".tmp", target);
            }
            catch
            {
                threw = true;
            }

            TestHelpers.AssertTrue(threw, "临时文件缺失应抛异常");
            TestHelpers.AssertStr("keep", File.ReadAllText(target), "目标不应被改动");
        });
    }
}