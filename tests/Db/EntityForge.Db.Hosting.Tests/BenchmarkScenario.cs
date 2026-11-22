using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;

namespace EntityForge.Db.Hosting.Tests;

[EntityForge.Db.Mapping.EntityType("public-benchmarks", "item")]
public sealed record BenchmarkItem(string Id, string Name);

internal sealed class BenchmarkScenario(int size, int concurrency, string workload,
    Func<BenchmarkItem, CancellationToken, Task> save, Func<string, CancellationToken, Task<BenchmarkItem?>> find,
    Func<string, CancellationToken, Task> delete, CancellationTokenSource deadline)
{
    internal BenchmarkItem[] Expected { get; } = Enumerable.Range(0, size).Select(i => new BenchmarkItem("key-" + i.ToString(CultureInfo.InvariantCulture), "initial-" + i.ToString(CultureInfo.InvariantCulture))).ToArray();

    internal int MeasuredWrites;
    private readonly ConcurrentDictionary<string, byte> admitted = new();
    private readonly ConcurrentDictionary<string, byte> acknowledged = new();

    internal string KeyDistribution => workload == "entity-hot-key" ? "single-hot-key" : "worker-striped";

    internal Func<int, Task>? Query
    {
        get; set;
    }

    internal bool QueryWrites
    {
        get; set;
    }

    internal async Task LoadAsync()
    {
        foreach (var item in Expected)
        {
            await save(item, deadline.Token);
        }
    }

    internal async Task VerifyAsync(int warmup = 0, int measured = 0)
    {
        foreach (var item in Expected)
        {
            if (workload == "entity-hot-key" && item.Id == "key-0" && acknowledged.Count > 0)
            {
                var actual = await find(item.Id, deadline.Token);
                Assert.IsNotNull(actual);
                Assert.AreEqual(item.Id, actual.Id);
                Assert.IsTrue(acknowledged.ContainsKey(actual.Name), "Final hot value must be acknowledged.");
                if (measured > 0)
                {
                    Assert.IsTrue(actual.Name.StartsWith("measured-", StringComparison.Ordinal), "Final read must include a measured overwrite.");
                }
            }
            else
            {
                Assert.AreEqual(item, await find(item.Id, deadline.Token));
            }
        }
        Assert.IsNull(await find("never-inserted", deadline.Token));
        if (workload == "entity-insert")
        {
            foreach (var phase in new[] { (Name: "warmup", Count: warmup), (Name: "measured", Count: measured) })
            {
                for (int i = 0; i < phase.Count; i++)
                {
                    var item = Inserted(phase.Name, i);
                    Assert.AreEqual(item, await find(item.Id, deadline.Token));
                }
            }
        }
    }

    private static BenchmarkItem Inserted(string phase, int index) => new("insert-" + phase + "-" + index.ToString(CultureInfo.InvariantCulture), "value-" + index.ToString(CultureInfo.InvariantCulture));

    private async Task ExecuteAsync(int index, int worker, string phase)
    {
        int key = worker + concurrency * ((index / concurrency) % ((size - 1 - worker) / concurrency + 1));
        var before = Expected[key];
        bool write = false;
        if (Query is not null)
        {
            await Query(key);
            write = QueryWrites;
        }
        else if (workload == "entity-hot-key")
        {
            var item = new BenchmarkItem("key-0", phase + "-" + index.ToString(CultureInfo.InvariantCulture));
            admitted.TryAdd(item.Name, 0);
            await save(item, deadline.Token);
            acknowledged.TryAdd(item.Name, 0);
            var actual = await find(item.Id, deadline.Token);
            Assert.IsNotNull(actual);
            Assert.AreEqual(item.Id, actual.Id);
            Assert.IsTrue(admitted.ContainsKey(actual.Name), "Read must observe an issued overwrite.");
            write = true;
        }
        else if (workload == "entity-insert")
        {
            var item = Inserted(phase, index);
            await save(item, deadline.Token);
            Assert.AreEqual(item, await find(item.Id, deadline.Token));
            write = true;
        }
        else if (workload == "entity-replace" || workload == "entity-mixed" && index % 5 == 0 || workload == "entity-balanced" && index % 2 == 0)
        {
            var item = before with
            {
                Name = phase + "-" + index.ToString(CultureInfo.InvariantCulture)
            };
            await save(item, deadline.Token);
            Assert.AreEqual(item, await find(item.Id, deadline.Token));
            Expected[key] = item;
            write = true;
        }
        else if (workload == "entity-delete-recreate")
        {
            await delete(before.Id, deadline.Token);
            Assert.IsNull(await find(before.Id, deadline.Token));
            await save(before, deadline.Token);
            Assert.AreEqual(before, await find(before.Id, deadline.Token));
            write = true;
        }
        else
        {
            Assert.AreEqual(before, await find(before.Id, deadline.Token));
        }
        if (write && phase == "measured")
        {
            Interlocked.Increment(ref MeasuredWrites);
        }
    }

    internal async Task<int> WindowAsync(int count, string phase, double[]? latency)
    {
        int completed = 0;
        await WorkloadTaskGroup.RunAsync(Enumerable.Range(0, concurrency).Select(worker => (Func<Task>)(async () =>
        {
            for (int index = worker; index < count; index += concurrency)
            {
                deadline.Token.ThrowIfCancellationRequested();
                long start = Stopwatch.GetTimestamp();
                await ExecuteAsync(index, worker, phase);
                if (latency is not null)
                {
                    latency[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }
                Interlocked.Increment(ref completed);
            }
        })).ToArray(), deadline);
        Assert.AreEqual(count, completed);
        return completed;
    }
}
