namespace KyjScada.Domain.Tests;

public sealed class TestRunner
{
    private int _pass;
    private int _fail;
    private int _skip;
    private readonly List<string> _failures = new();

    public void Run(string name, Action body)
    {
        try
        {
            body();
            _pass++;
            Console.WriteLine("  [PASS] " + name);
        }
        catch (SkipTestException ex)
        {
            _skip++;
            Console.WriteLine("  [SKIP] " + name);
            Console.WriteLine("         " + ex.Message);
        }
        catch (Exception ex)
        {
            _fail++;
            _failures.Add(name + " -> " + ex.Message);
            Console.WriteLine("  [FAIL] " + name);
            Console.WriteLine("         " + ex.Message);
        }
    }

    public int Report()
    {
        Console.WriteLine();
        Console.WriteLine("通过 " + _pass + " / 失败 " + _fail + (_skip > 0 ? " / 跳过 " + _skip : ""));

        if (_fail > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败明细:");
            foreach (var f in _failures)
            {
                Console.WriteLine("  - " + f);
            }
        }

        // SKIP 不能悄悄算通过:全被跳过时 CI 仍返回 0,等于"什么都没验证"也报绿。
        // 默认口径:有失败 → 1;全跳过 → 1;零星跳过 → 0 但要打警告;KYJ_TEST_STRICT=1 时任何跳过都判红。
        if (_skip > 0)
        {
            if (Environment.GetEnvironmentVariable("KYJ_TEST_STRICT") == "1")
            {
                Console.WriteLine();
                Console.WriteLine("✘ 严格模式(KYJ_TEST_STRICT=1):有 " + _skip + " 条用例被跳过,判为失败");

                return 1;
            }

            if (_pass == 0)
            {
                Console.WriteLine();
                Console.WriteLine("✘ 全部 " + _skip + " 条用例都被跳过,没有任何断言真正执行");

                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("⚠ 有 " + _skip + " 条用例被跳过,未验证;发版前请用 KYJ_TEST_STRICT=1 复跑。");
        }

        return _fail == 0 ? 0 : 1;
    }
}
