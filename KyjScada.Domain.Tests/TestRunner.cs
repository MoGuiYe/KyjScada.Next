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

        return _fail == 0 ? 0 : 1;
    }
}
