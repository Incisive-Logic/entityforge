namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class WorkloadResourceSamplerTests
{
    [TestMethod]
    [DataRow(0L, 12L * 1024 * 1024 * 1024)]
    [DataRow(1L, 1L)]
    [DataRow(4L * 1024 * 1024 * 1024, 2L * 1024 * 1024 * 1024)]
    [DataRow(64L * 1024 * 1024 * 1024, 12L * 1024 * 1024 * 1024)]
    [DataRow(long.MaxValue, 12L * 1024 * 1024 * 1024)]
    public void Memory_budget_reserves_headroom_and_has_a_fixed_ceiling(long available, long expected) =>
        Assert.AreEqual(expected, WorkloadResourceSampler.DefaultWorkingSetLimit(available));

    [TestMethod]
    public async Task Memory_limit_cancels_workload_and_preserves_the_triggering_observation()
    {
        var directories = new TemporaryTestDirectories("entityforge-sampler-budget");
        try
        {
            var sampler = new WorkloadResourceSampler([directories.Create()], maximumWorkingSetBytes: 1);
            using var deadline = new CancellationTokenSource();
            var failure = await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await sampler.RunAsync(CancellationToken.None, deadline));
            StringAssert.Contains(failure.Message, "working set");
            Assert.IsTrue(deadline.IsCancellationRequested);
            Assert.AreEqual(1, sampler.Samples.Count);
            Assert.IsTrue(sampler.Samples[0].WorkingSetBytes > sampler.MaximumWorkingSetBytes);
        }
        finally
        {
            directories.Cleanup();
        }
    }

    [TestMethod]
    public async Task Published_resource_snapshot_stays_unchanged_when_sampling_finishes()
    {
        var directories = new TemporaryTestDirectories("entityforge-sampler-snapshot");
        try
        {
            var sampler = new WorkloadResourceSampler([directories.Create()], maximumWorkingSetBytes: long.MaxValue);
            using var deadline = new CancellationTokenSource();
            using var stop = new CancellationTokenSource();
            Task sampling = sampler.RunAsync(stop.Token, deadline);
            var beforeStop = sampler.Samples;
            await stop.CancelAsync();
            await sampling;
            Assert.AreEqual(1, beforeStop.Count, "A published snapshot must not gain later samples.");
            Assert.AreEqual(2, sampler.Samples.Count);
            Assert.AreEqual(beforeStop[0], sampler.Samples[0]);
        }
        finally
        {
            directories.Cleanup();
        }
    }

    [TestMethod]
    public async Task Normal_sampler_stop_preserves_final_sample_without_cancelling_workload()
    {
        var directories = new TemporaryTestDirectories("entityforge-sampler-stop");
        try
        {
            var sampler = new WorkloadResourceSampler([directories.Create()], maximumWorkingSetBytes: long.MaxValue);
            using var deadline = new CancellationTokenSource();
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            double before = sampler.ElapsedSeconds;
            await sampler.RunAsync(stop.Token, deadline);
            double after = sampler.ElapsedSeconds;
            Assert.IsFalse(deadline.IsCancellationRequested);
            Assert.AreEqual(System.Runtime.GCSettings.IsServerGC, sampler.GarbageCollector.Server);
            Assert.AreEqual(System.Runtime.GCSettings.LatencyMode.ToString(), sampler.GarbageCollector.LatencyMode);
            Assert.IsTrue(sampler.GarbageCollector.Configuration.Count > 0);
            Assert.AreEqual(2, sampler.Samples.Count);
            Assert.IsTrue(sampler.Samples.All(sample => sample.ProcessIo is not null));
            Assert.IsTrue(sampler.Samples.All(sample => sample.Seconds >= before && sample.Seconds <= after),
                "Phase boundaries must use the same monotonic clock as resource samples.");
            foreach (var sample in sampler.Samples)
            {
                VerifyGenerations(sample.LastGcIndex, sample.LastGcGenerations);
                foreach (var collection in new[] { sample.LastBlockingGen2, sample.LastBackgroundGen2 }.OfType<WorkloadResourceSampler.FullCollection>())
                {
                    VerifyGenerations(collection.Index, collection.Generations);
                }
            }
        }
        finally
        {
            directories.Cleanup();
        }
    }

    [TestMethod]
    [DataRow(300L, 50L, true)]
    [DataRow(301L, 50L, false)]
    [DataRow(300L, 51L, false)]
    public void Generation_consistency_flags_preserve_disagreeing_runtime_observations(long heap, long fragmented, bool expected)
    {
        var collection = new WorkloadResourceSampler.FullCollection(1, heap, fragmented, 400, false, false, 0)
        {
            Generations = [new(0, 100, 20, 100, 20), new(1, 200, 30, 200, 30)]
        };
        Assert.AreEqual(expected, collection.GenerationTotalsMatch);
        Assert.AreEqual(heap, collection.HeapBytes);
        Assert.AreEqual(fragmented, collection.FragmentedBytes);
        Assert.IsNull(new WorkloadResourceSampler.FullCollection(0, 0, 0, 0, false, false, 0).GenerationTotalsMatch);
    }

    [TestMethod]
    [DataRow(6291488L, 16592L, 0L, 64L, false)]
    [DataRow(6291472L, 6128L, 0L, 664L, false)]
    [DataRow(100L, 10L, 80L, 5L, true)]
    [DataRow(0L, 0L, 0L, 0L, true)]
    [DataRow(100L, 101L, 80L, 5L, false)]
    [DataRow(100L, -1L, 80L, 5L, false)]
    public void Generation_bounds_flag_preserves_raw_observations_in_reports(
        long before, long fragmentedBefore, long after, long fragmentedAfter, bool expected)
    {
        // The first two rows reproduce recorded .NET Linux observations;
        // they must remain visible rather than be rejected or normalized.
        var generation = new WorkloadResourceSampler.GenerationMemory(0, before, fragmentedBefore, after, fragmentedAfter);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(generation));
        Assert.AreEqual(expected, json.RootElement.GetProperty("BoundsConsistent").GetBoolean());
        Assert.AreEqual(before, json.RootElement.GetProperty("SizeBeforeBytes").GetInt64());
        Assert.AreEqual(fragmentedBefore, json.RootElement.GetProperty("FragmentedBeforeBytes").GetInt64());
        Assert.AreEqual(after, json.RootElement.GetProperty("SizeAfterBytes").GetInt64());
        Assert.AreEqual(fragmentedAfter, json.RootElement.GetProperty("FragmentedAfterBytes").GetInt64());
    }

    private static void VerifyGenerations(long index, IReadOnlyList<WorkloadResourceSampler.GenerationMemory> generations)
    {
        if (index == 0)
        {
            Assert.AreEqual(0, generations.Count);
            return;
        }
        Assert.AreEqual(5, generations.Count);
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, generations.Select(item => item.Index).ToArray());
        // Counter consistency belongs in the report, not a test of the
        // external runtime: see the recorded-observation cases above.
    }
}
