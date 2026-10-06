namespace TypePilot.Tests;

internal static class Checks
{
    public static int Passed { get; private set; }
    public static void True(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Passed++; Console.WriteLine("PASS: " + name);
    }
    public static void Equal<T>(T expected, T actual, string name) => True(EqualityComparer<T>.Default.Equals(expected, actual), name + $" (expected {expected}, actual {actual})");
    public static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); } catch (T) { True(true, name); return; }
        throw new Exception("FAIL: " + name);
    }
}
