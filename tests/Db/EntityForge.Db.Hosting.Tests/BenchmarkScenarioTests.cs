namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class BenchmarkScenarioTests
{
    [TestMethod]
    public async Task Write_then_query_operations_are_counted_as_mutations_only_when_verified()
    {
        using var deadline = new CancellationTokenSource();
        var scenario = new BenchmarkScenario(4, 2, "semantic-gql-after-write", (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<BenchmarkItem?>(null), (_, _) => Task.CompletedTask, deadline)
        {
            QueryWrites = true,
            Query = _ => Task.CompletedTask
        };
        await scenario.WindowAsync(2, "warmup", null);
        Assert.AreEqual(0, scenario.MeasuredWrites);
        await scenario.WindowAsync(8, "measured", new double[8]);
        Assert.AreEqual(8, scenario.MeasuredWrites);
        scenario.Query = _ => throw new AssertFailedException("Stale query result");
        await Assert.ThrowsExactlyAsync<AssertFailedException>(() => scenario.WindowAsync(1, "measured", new double[1]));
        Assert.AreEqual(8, scenario.MeasuredWrites);
    }

    [TestMethod]
    public async Task Hot_key_oracle_rejects_a_value_that_was_never_issued()
    {
        using var deadline = new CancellationTokenSource();
        var scenario = new BenchmarkScenario(1, 1, "entity-hot-key", (_, _) => Task.CompletedTask,
            (id, _) => Task.FromResult<BenchmarkItem?>(new(id, "unissued")), (_, _) => Task.CompletedTask, deadline);
        await Assert.ThrowsExactlyAsync<AssertFailedException>(() => scenario.WindowAsync(1, "measured", new double[1]));
    }

    [TestMethod]
    public async Task Hot_key_final_oracle_rejects_a_stale_warmup_value()
    {
        using var deadline = new CancellationTokenSource();
        BenchmarkItem? stored = null;
        var scenario = new BenchmarkScenario(1, 1, "entity-hot-key",
            (item, _) =>
            {
                stored = item;
                return Task.CompletedTask;
            },
            (id, _) => Task.FromResult(id == "key-0" ? stored : null), (_, _) => Task.CompletedTask, deadline);
        await scenario.LoadAsync();
        await scenario.WindowAsync(1, "warmup", null);
        var stale = stored;
        await scenario.WindowAsync(1, "measured", new double[1]);
        await scenario.VerifyAsync(1, 1);
        stored = stale;
        await Assert.ThrowsExactlyAsync<AssertFailedException>(() => scenario.VerifyAsync(1, 1));
    }
}
