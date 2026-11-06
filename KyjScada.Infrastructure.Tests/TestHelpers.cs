namespace KyjScada.Infrastructure.Tests;

public static class TestHelpers
{
    public static void AssertEq(int expected, int actual, string what)
    {
        if (expected != actual)
        {
            throw new Exception(what + ": 期望 " + expected + ",实际 " + actual);
        }
    }

    public static void AssertStr(string expected, string actual, string what)
    {
        if (expected != actual)
        {
            throw new Exception(what + ": 期望 [" + expected + "],实际 [" + actual + "]");
        }
    }

    public static void AssertTrue(bool condition, string what)
    {
        if (!condition)
        {
            throw new Exception(what + " 不成立");
        }
    }
}
