namespace KyjScada.Domain.Tests;

public static class Assert
{
    public static void True(bool condition, string what)
    {
        if (!condition)
        {
            throw new Exception(what + " 不成立");
        }
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception(what + ": 期望 [" + expected + "],实际 [" + actual + "]");
        }
    }

    public static void Near(double expected, double actual, string what, double tol = 1e-6)
    {
        if (Math.Abs(expected - actual) > tol)
        {
            throw new Exception(what + ": 期望 [" + expected + "],实际 [" + actual + "]");
        }
    }

    public static void Contains(string haystack, string needle, string what)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new Exception(what + ": 未找到 " + needle);
        }
    }

    public static void Throws(Action body, string what)
    {
        try
        {
            body();
        }
        catch
        {
            return;
        }

        throw new Exception(what + ": 期望抛出异常但没有");
    }
}
