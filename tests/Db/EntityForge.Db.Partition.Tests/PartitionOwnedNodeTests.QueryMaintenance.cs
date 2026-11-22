using System.Diagnostics;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Semantic;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    [DataRow(DatabaseProfileId.Semantic, true)]
    public async Task Two_query_homes_preserve_results_across_mixed_writes_and_repeated_retention(DatabaseProfileId profile, bool timestamps)
    {
        const int entities = 128, rounds = 4, writers = 32, readers = 8, minimumReadsPerReader = 64;
        var minimumMixedDuration = TimeSpan.FromSeconds(10);
        await WithProfileFragments(profile, async (left, right, publicLeft, publicRight, http, nodes, authority) =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var token = deadline.Token;
            Func<int, string, Task> write;
            Func<HttpPartitionEntityClient, ValueTask<PartitionQueryResult>> query;
            if (profile == DatabaseProfileId.PropertyGraph)
            {
                var store = new HttpGraphEntityClient(http, right.Generation).EntityStore<Item>();
                write = async (index, value) =>
                {
                    var captured = store.CaptureSave(new("maintenance-" + index, value), new(EntityOperationIds.New()));
                    await right.DeliverAsync(captured.OperationId, captured.Export(), token);
                };
                query = client => client.QueryGqlAsync($"MATCH (n) RETURN n LIMIT {entities}", null, token);
            }
            else if (profile == DatabaseProfileId.Rdf)
            {
                await right.UpdateQueryAsync(new(right.Generation, profile, EntityOperationIds.New(), "sparql",
                    "INSERT DATA { " + string.Join(" ", Enumerable.Range(0, entities).Select(i =>
                        $"<urn:maintenance:{i}> <urn:name> \"initial\" .")) + " }"), token);
                write = async (index, value) => await right.UpdateQueryAsync(new(right.Generation, profile, EntityOperationIds.New(), "sparql",
                    $"DELETE {{ <urn:maintenance:{index}> <urn:name> ?old }} INSERT {{ <urn:maintenance:{index}> <urn:name> \"{value}\" }} WHERE {{ <urn:maintenance:{index}> <urn:name> ?old }}"), token);
                query = client => client.QuerySparqlAsync("SELECT ?s ?name WHERE { ?s <urn:name> ?name } ORDER BY ?s", null, token);
            }
            else
            {
                var store = new HttpSemanticEntityClient(http, right.Generation).EntityStore<Item>();
                write = async (index, value) =>
                {
                    var captured = store.CaptureSave(new("maintenance-" + index, value), SemanticEntityWriteOperation.Create());
                    await right.DeliverAsync(captured.OperationId, captured.Export(), token);
                };
                var manifest = store.Manifests[0];
                var property = manifest.ProfileBindings.Single(p => p.MemberIdentity == manifest.Members.Single(m => m.ClrName == "Name").Identity).StoredBinding;
                var selection = new PortableEntityQueryRequest(manifest.ProfileTypeBinding,
                    new(manifest.ProfileTypeBinding, Fields: [new(property, "name")]), ConceptMatchMode: "direct");
                query = client => client.QuerySemanticAsync(selection, null, token);
            }
            if (profile != DatabaseProfileId.Rdf)
            {
                for (int index = 0; index < entities; index++)
                {
                    await write(index, "initial");
                }
            }

            string rows = profile == DatabaseProfileId.PropertyGraph ? "matches" : profile == DatabaseProfileId.Rdf ? "bindings" : "entities";
            for (int round = 0; round < rounds; round++)
            {
                await query(publicLeft);
                await query(publicRight);
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                int completedWrites = 0, completedReads = 0, readsDuringWrites = 0, writerFinished = 0;
                long attempts = 0, refreshes = 0, peakBytes = 0;
                int peakCuts = 0, peakJobs = 0, peakProtections = 0;
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
                using var sampling = CancellationTokenSource.CreateLinkedTokenSource(token);
                var sampler = Task.Run(async () =>
                {
                    try
                    {
                        while (true)
                        {
                            peakBytes = Math.Max(peakBytes, nodes.Sum(n => n.RetainedQueryWorkspaceBytes));
                            peakCuts = Math.Max(peakCuts, nodes.Sum(n => n.UnreleasedReadCutCount));
                            peakJobs = Math.Max(peakJobs, nodes.Sum(n => n.ActiveQueryJobCount));
                            peakProtections = Math.Max(peakProtections, nodes.Sum(n => n.Kernel.TimestampProtectionCount));
                            await Task.Delay(TimeSpan.FromMilliseconds(10), sampling.Token);
                        }
                    }
                    catch (OperationCanceledException) when (sampling.IsCancellationRequested)
                    {

                    }
                });
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    var reading = Enumerable.Range(0, readers).Select(async index =>
                    {
                        await start.Task.WaitAsync(token);
                        for (int count = 0; count < minimumReadsPerReader || Volatile.Read(ref writerFinished) == 0; count++)
                        {
                            var answer = await query(index % 2 == 0 ? publicLeft : publicRight);
                            Assert.AreEqual(entities, answer.Value.GetProperty(rows).GetArrayLength());
                            Interlocked.Increment(ref completedReads);
                            if (Volatile.Read(ref writerFinished) == 0)
                            {
                                Interlocked.Increment(ref readsDuringWrites);
                            }
                        }
                    }).ToArray();
                    var writing = Task.Run(async () =>
                    {
                        await start.Task.WaitAsync(token);
                        try
                        {
                            for (int index = 0; index < writers || stopwatch.Elapsed < minimumMixedDuration; index++)
                            {
                                await write(index % entities, $"round-{round}-write-{index}");
                                Interlocked.Increment(ref completedWrites);
                            }
                        }
                        finally
                        {
                            Volatile.Write(ref writerFinished, 1);
                        }
                    });
                    start.SetResult();
                    await Task.WhenAll(reading.Append(writing));
                }
                finally
                {
                    sampling.Cancel();
                    await sampler;
                }
                stopwatch.Stop();
                Assert.IsTrue(readsDuringWrites > 0);
                // Retention must preserve the data version. Exclude independent background
                // physical commits from this exact-version assertion, as checkpoint capture does.
                var checkpointPriorities = nodes.Select(node =>
                    typeof(EntityForge.Db.Kernel.DatabaseKernel).GetField("_profile",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(node.Kernel))
                    .OfType<EntityForge.Db.Kernel.IDatabaseProfileCheckpointPriority>()
                    .Select(profile => profile.EnterCheckpointPriority()).ToArray();
                try
                {
                    var before = nodes.Select(n => n.Kernel.CurrentVersion.Value).ToArray();
                    var a = await query(publicLeft);
                    var b = await query(publicRight);
                    Assert.AreEqual(Canonical(a.Value, true), Canonical(b.Value, true));
                    StringAssert.Contains(a.Value.GetRawText(), $"round-{round}-write-{completedWrites - 1}");
                    Assert.AreEqual(0, attempts - completedReads - 2, "Timestamp reads must not use native-version invalidation retries.");
                    await left.CloseReadEpochAsync();
                    await right.CloseReadEpochAsync();
                    var maintenance = Stopwatch.StartNew();
                    await PartitionRetention.RetainAsync(authority, nodes, DateTimeOffset.UtcNow, token);
                    maintenance.Stop();
                    foreach (var node in nodes)
                    {
                        Assert.AreEqual(0L, node.RetainedQueryWorkspaceBytes);
                        Assert.AreEqual(0, node.ActiveQueryJobCount);
                        Assert.AreEqual(0, node.UnreleasedReadCutCount);
                        Assert.AreEqual(0, node.Kernel.TimestampProtectionCount);
                    }
                    Assert.IsTrue(nodes.Select(n => n.Kernel.CurrentVersion.Value).SequenceEqual(before), $"Versions before retention: {string.Join(",", before)}; after: {string.Join(",", nodes.Select(n => n.Kernel.CurrentVersion.Value))}");
                    // Fresh identities must fall strictly above the installed millisecond floor.
                    await Task.Delay(TimeSpan.FromMilliseconds(5), token);
                    var after = await query(publicRight);
                    Assert.AreEqual(Canonical(b.Value, true), Canonical(after.Value, true));
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        profile = profile.ToString(),
                        timestamps,
                        round,
                        entities,
                        readers,
                        reads = completedReads,
                        writes = completedWrites,
                        readsDuringWrites,
                        attempts,
                        refreshes,
                        peakBytes,
                        peakCuts,
                        peakJobs,
                        peakProtections,
                        mixedSeconds = stopwatch.Elapsed.TotalSeconds,
                        maintenanceMs = maintenance.Elapsed.TotalMilliseconds,
                        versions = before,
                        resourcesDrainedBeforeReadback = true,
                        maintenancePreservedDataVersions = true
                    }));
                }
                finally
                {
                    foreach (var priority in checkpointPriorities)
                    {
                        priority.Dispose();
                    }
                }
            }
            await left.CloseReadEpochAsync();
            await right.CloseReadEpochAsync();
        }, bothCompact: true, timestamps: timestamps);
    }
}
