namespace EntityForge.Db.Hosting.Tests;

internal sealed class WorkloadKeyDistribution
{
    internal WorkloadKeyDistribution(string mode, int keyCount)
    {
        if (mode is not ("uniform" or "hotspot"))
        {
            throw new ArgumentException("KEY_DISTRIBUTION must be uniform or hotspot.", nameof(mode));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(keyCount, 1);
        Mode = mode;
        KeyCount = keyCount;
        HotKeyCount = Math.Max(1, keyCount / 5);
    }

    internal string Mode
    {
        get;
    }

    internal int KeyCount
    {
        get;
    }

    internal int HotKeyCount
    {
        get;
    }

    internal int Next(Random random) => Mode == "uniform" || KeyCount == 1
        ? random.Next(KeyCount)
        : random.Next(100) < 80 ? random.Next(HotKeyCount) : random.Next(HotKeyCount, KeyCount);
}
