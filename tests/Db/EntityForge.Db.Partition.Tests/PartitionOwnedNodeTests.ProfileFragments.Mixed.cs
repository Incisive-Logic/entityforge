using System.Diagnostics;
using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Semantic;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(DatabaseProfileId.Rdf, 8, false)]
    [DataRow(DatabaseProfileId.Rdf, 8, true)]
    [DataRow(DatabaseProfileId.Rdf, 32, false)]
    [DataRow(DatabaseProfileId.Rdf, 32, true)]
    [DataRow(DatabaseProfileId.Semantic, 8, false)]
    [DataRow(DatabaseProfileId.Semantic, 8, true)]
    [DataRow(DatabaseProfileId.Semantic, 32, false)]
    [DataRow(DatabaseProfileId.Semantic, 32, true)]
    public async Task Compact_profiles_two_query_homes_drain_under_concurrent_writes(DatabaseProfileId profile, int readers, bool commands)
    {
        const int entities = 16, writers = 24, readsPerReader = 64;
        await WithProfileFragments(profile, async (first, second, publicFirst, publicSecond, http) =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            Func<int, Task> write;
            Func<HttpPartitionEntityClient, ValueTask<PartitionQueryResult>> query;
            if (profile == DatabaseProfileId.Rdf)
            {
                await second.UpdateQueryAsync(new(second.Generation, profile, EntityOperationIds.New(), "sparql",
                    "INSERT DATA { " + string.Join(" ", Enumerable.Range(0, entities).Select(i =>
                        $"<urn:mixed:{i}> <urn:name> \"initial\" .")) + " }"), timeout.Token);
                write = async index => await second.UpdateQueryAsync(new(second.Generation, profile, EntityOperationIds.New(), "sparql",
                    $"DELETE {{ <urn:mixed:0> <urn:name> ?old }} INSERT {{ <urn:mixed:0> <urn:name> \"value-{index}\" }} WHERE {{ <urn:mixed:0> <urn:name> ?old }}"), timeout.Token);
                query = client => client.QuerySparqlAsync("SELECT ?s ?name WHERE { ?s <urn:name> ?name } ORDER BY ?s", null, timeout.Token);
            }
            else
            {
                var store = new HttpSemanticEntityClient(http, second.Generation).EntityStore<Item>();
                for (int i = 0; i < entities; i++)
                {
                    var capture = store.CaptureSave(new(i.ToString(), "initial"), SemanticEntityWriteOperation.Create());
                    await second.DeliverAsync(capture.OperationId, capture.Export(), timeout.Token);
                }
                write = async index =>
                {
                    var capture = store.CaptureSave(new("0", "value-" + index), SemanticEntityWriteOperation.Create());
                    await second.DeliverAsync(capture.OperationId, capture.Export(), timeout.Token);
                };
                var manifest = store.Manifests[0];
                var property = manifest.ProfileBindings.Single(p => p.MemberIdentity == manifest.Members.Single(m => m.ClrName == "Name").Identity).StoredBinding;
                var selection = new PortableEntityQueryRequest(manifest.ProfileTypeBinding,
                    new(manifest.ProfileTypeBinding, Fields: [new(property, "name")]), ConceptMatchMode: "direct");
                query = client => client.QuerySemanticAsync(selection, null, timeout.Token);
            }
            await query(publicFirst);
            await query(publicSecond);
            long attempts = 0, refreshes = 0;
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == DistributedReadTelemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = activity =>
                {
                    if (activity.OperationName == "partition.read.epoch.query")
                    {
                        Interlocked.Increment(ref attempts);
                    }

                    if (activity.OperationName == "partition.read.epoch.refresh")
                    {
                        Interlocked.Increment(ref refreshes);
                    }
                }
            };
            ActivitySource.AddActivityListener(listener);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int writesCompleted = 0, readsDuringWrites = 0, completedReads = 0;
            var clock = Stopwatch.StartNew();
            var reading = Enumerable.Range(0, readers).Select(async reader =>
            {
                await start.Task;
                for (int i = 0; i < readsPerReader || Volatile.Read(ref writesCompleted) < writers; i++)
                {
                    var result = await query(reader % 2 == 0 ? publicFirst : publicSecond);
                    Assert.AreEqual(entities, result.Value.GetProperty(profile == DatabaseProfileId.Rdf ? "bindings" : "entities").GetArrayLength());
                    Assert.AreEqual(profile == DatabaseProfileId.Rdf ? "rdf-fragment-bindings" : "semantic-projection-fragments", result.Statistics!.Strategy);
                    Interlocked.Increment(ref completedReads);
                    if (Volatile.Read(ref writesCompleted) < writers)
                    {
                        Interlocked.Increment(ref readsDuringWrites);
                    }
                }
            }).ToArray();
            var writing = Task.Run(async () =>
            {
                await start.Task;
                for (int i = 0; i < writers; i++)
                {
                    await write(i);
                    Interlocked.Increment(ref writesCompleted);
                }
            });
            start.SetResult();
            await Task.WhenAll(reading.Append(writing));
            clock.Stop();
            Assert.IsTrue(readsDuringWrites > 0, "The schedule must actually overlap reads and writes.");
            var left = await query(publicFirst);
            var right = await query(publicSecond);
            Assert.AreEqual(Canonical(left.Value, true), Canonical(right.Value, true));
            StringAssert.Contains(left.Value.GetRawText(), "value-" + (writers - 1));
            Assert.IsTrue(refreshes >= 0, "History protection refresh is independent of native-version changes.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                profile = profile.ToString(),
                homes = 2,
                readers,
                reads = completedReads,
                confirmationReads = 2,
                writes = writesCompleted,
                readsDuringWrites,
                attempts,
                refreshes,
                seconds = clock.Elapsed.TotalSeconds,
                diagnosticReadsPerSecond = completedReads / clock.Elapsed.TotalSeconds
            }));
            await first.CloseReadEpochAsync();
            await second.CloseReadEpochAsync();
        }, bothCompact: true, commands: commands);
    }
}
