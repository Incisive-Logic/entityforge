namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public void Workload_histogram_bounds_exact_nearest_rank_results_with_fixed_storage()
    {
        var histogram = new WorkloadLatencyHistogram();
        Assert.IsNull(histogram.Percentile(.95));
        var random = new Random(39075);
        long[] ticks = Enumerable.Range(0, 100_000).Select(_ => random.NextInt64(0, TimeSpan.TicksPerHour + 1))
            .Concat([0L, 1L, 64L, 65L, 128L, 129L, TimeSpan.TicksPerHour]).Order().ToArray();
        int buckets = histogram.BucketCount;
        foreach (long value in ticks)
        {
            histogram.Add(TimeSpan.FromTicks(value));
        }
        foreach (double fraction in new[] { .001, .5, .95, .99, 1 })
        {
            double exact = ticks[(int)Math.Ceiling(ticks.Length * fraction) - 1] / (double)TimeSpan.TicksPerMillisecond;
            var interval = histogram.Percentile(fraction)!;
            Assert.IsTrue(interval.LowerMilliseconds <= exact && exact <= interval.UpperMilliseconds);
            Assert.IsTrue(interval.UpperMilliseconds - interval.LowerMilliseconds <= Math.Max(.0001, exact / 64));
        }
        Assert.AreEqual(buckets, histogram.BucketCount);
        Assert.IsTrue(buckets < 2048);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => histogram.Add(TimeSpan.FromTicks(-1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => histogram.Add(TimeSpan.FromHours(1) + TimeSpan.FromTicks(1)));
    }

    [TestMethod]
    public async Task Workload_histogram_preserves_concurrent_counts_and_empty_percentiles()
    {
        var histogram = new WorkloadLatencyHistogram();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int index = 0; index < 10_000; index++)
            {
                histogram.Add(TimeSpan.FromTicks(64));
            }
        })));
        using var report = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(histogram.Summary()));
        Assert.AreEqual(80_000L, report.RootElement.GetProperty("count").GetInt64());
        Assert.AreEqual(80_000L, report.RootElement.GetProperty("bucketCounts").EnumerateArray().Sum(item => item.GetInt64()));
        Assert.AreEqual(.0064, histogram.Percentile(.99)!.LowerMilliseconds);
        Assert.AreEqual(.0064, histogram.Percentile(.99)!.UpperMilliseconds);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => histogram.Percentile(Double.NaN));
    }
}
