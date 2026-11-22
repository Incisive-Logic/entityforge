namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class WorkloadKeyDistributionTests
{
    [TestMethod]
    public void Uniform_keys_preserve_the_previous_seeded_sequence()
    {
        var expected = new Random(39075);
        var actual = new Random(39075);
        var distribution = new WorkloadKeyDistribution("uniform", 103);
        for (int i = 0; i < 1000; i++)
        {
            Assert.AreEqual(expected.Next(103), distribution.Next(actual));
        }
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(5)]
    [DataRow(103)]
    public void Hotspot_keys_are_reproducible_and_concentrate_traffic_in_a_bounded_prefix(int count)
    {
        var distribution = new WorkloadKeyDistribution("hotspot", count);
        var first = new Random(39075);
        var second = new Random(39075);
        int hot = 0;
        var seen = new HashSet<int>();
        for (int i = 0; i < 10_000; i++)
        {
            int key = distribution.Next(first);
            Assert.AreEqual(key, distribution.Next(second));
            Assert.IsTrue(key >= 0 && key < count);
            seen.Add(key);
            if (key < distribution.HotKeyCount)
            {
                hot++;
            }
        }

        Assert.IsTrue(hot is >= 7700 and <= 8300, "The seeded sample should approximate the declared 80% hotspot traffic.");
        Assert.AreEqual(count, seen.Count, "The cold region must remain reachable.");
    }

    [TestMethod]
    public void Single_key_and_invalid_distribution_settings_are_explicit()
    {
        var distribution = new WorkloadKeyDistribution("hotspot", 1);
        Assert.AreEqual(1, distribution.HotKeyCount);
        Assert.AreEqual(0, distribution.Next(new Random(39075)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new WorkloadKeyDistribution("uniform", 0));
        Assert.ThrowsExactly<ArgumentException>(() => new WorkloadKeyDistribution("zipf", 10));
    }
}
