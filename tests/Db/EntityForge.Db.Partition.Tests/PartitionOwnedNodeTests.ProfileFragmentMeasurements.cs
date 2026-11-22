using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Semantic;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Compact_profile_operator_measurement(DatabaseProfileId profile)
    {
        string? directory = Environment.GetEnvironmentVariable("ENTITYFORGE_PROFILE_FRAGMENT_MEASURE");
        if (string.IsNullOrWhiteSpace(directory))
        {
            Assert.Inconclusive("Opt-in nominal measurement.");
            return;
        }
        const int entities = 256, seedBatch = 32, warmup = 32, repetitions = 3;
        bool shardServing = Environment.GetEnvironmentVariable("ENTITYFORGE_PROFILE_SHARD_SERVING_MEASURE") == "1";
        var results = new List<object>();
        await WithProfileFragments(profile, async (control, candidate, publicControl, publicCandidate, http, nodes, _) =>
        {
            string? concept = null, property = null;
            string[] projectedIdentities = [];
            if (profile == DatabaseProfileId.Rdf)
            {
                for (int start = 0; start < entities; start += seedBatch)
                {
                    string data = string.Join(" ", Enumerable.Range(start, seedBatch).Select(i =>
                        $"<urn:item:{i}> <urn:id> \"{i}\" . <urn:item:{i}> <urn:name> \"value-{i % 4}\" ."));
                    await candidate.UpdateQueryAsync(new(candidate.Generation, profile, EntityOperationIds.New(), "sparql", "INSERT DATA { " + data + " }"), default);
                }
            }
            else
            {
                var store = new HttpSemanticEntityClient(http, candidate.Generation).EntityStore<Item>();
                for (int i = 0; i < entities; i++)
                {
                    var capture = store.CaptureSave(new(i.ToString(), "value-" + i % 4), SemanticEntityWriteOperation.Create());
                    await candidate.DeliverAsync(capture.OperationId, capture.Export());
                }
                var manifest = store.Manifests[0];
                concept = manifest.ProfileTypeBinding;
                property = manifest.ProfileBindings.Single(p => p.MemberIdentity == manifest.Members.Single(m => m.ClrName == "Name").Identity).StoredBinding;
                var all = await publicControl.QuerySemanticAsync(new(concept, new(concept), ConceptMatchMode: "direct"));
                projectedIdentities = all.Value.GetProperty("entities").EnumerateArray().Select(entity => entity.GetProperty("identity").GetString()!).ToArray();
                Assert.AreEqual(entities, projectedIdentities.Length);
            }
            foreach (string shape in new[] { "point", "filtered-projection" })
            {
                ValueTask<PartitionQueryResult> Query(HttpPartitionEntityClient client, int index)
                {
                    if (profile == DatabaseProfileId.Rdf)
                    {
                        return client.QuerySparqlAsync(shape == "point"
                            ? $"SELECT ?s ?name WHERE {{ ?s <urn:id> \"{index % entities}\" . ?s <urn:name> ?name }}"
                            : "SELECT ?s ?name WHERE { ?s <urn:id> ?id . ?s <urn:name> ?name FILTER (?name = \"value-2\") } ORDER BY ?s");
                    }

                    return client.QuerySemanticAsync(new(concept!, new(concept!, Fields: [new(property!, "name")]),
                        Filter: shape == "point" ? null : new("propertycomparison", property, "equal", new(JsonSerializer.SerializeToElement("value-2"))),
                        Identities: shape == "point" ? [projectedIdentities[index % entities]] : null, ConceptMatchMode: "direct"));
                }
                var expected = await Query(publicControl, 0);
                var actual = await Query(publicCandidate, 0);
                Assert.AreEqual(Canonical(expected.Value), Canonical(actual.Value));
                int expectedCount = shape == "point" ? 1 : entities / 4;
                Assert.AreEqual(expectedCount, actual.Value.GetProperty(profile == DatabaseProfileId.Rdf ? "bindings" : "entities").GetArrayLength());
                foreach (int concurrency in new[] { 1, 32 })
                {
                    for (int repetition = 0; repetition < repetitions; repetition++)
                    {
                        foreach (bool compact in repetition % 2 == 0 ? new[] { false, true } : new[] { true, false })
                        {
                            await control.CloseReadEpochAsync();
                            await candidate.CloseReadEpochAsync();
                            var client = compact ? publicCandidate : publicControl;
                            for (int i = 0; i < warmup; i++)
                            {
                                await Query(client, i);
                            }

                            bool longerPoint = shape == "point" && profile == DatabaseProfileId.Rdf &&
                                Environment.GetEnvironmentVariable("ENTITYFORGE_PROFILE_FRAGMENT_LONG_POINT") == "1";
                            int count = shardServing ? shape == "point" ? concurrency == 1 ? 1024 : 8192 : concurrency == 1 ? 256 : 1024 :
                                longerPoint ? concurrency == 1 ? 4096 : 32768 : concurrency == 1 ? 512 : 2048;
                            var latencies = new double[count];
                            int next = -1;
                            using var process = Process.GetCurrentProcess();
                            var cpu = process.TotalProcessorTime;
                            long allocated = GC.GetTotalAllocatedBytes();
                            var watch = Stopwatch.StartNew();
                            await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
                            {
                                while (true)
                                {
                                    int index = Interlocked.Increment(ref next);
                                    if (index >= count)
                                    {
                                        break;
                                    }

                                    long start = Stopwatch.GetTimestamp();
                                    var result = await Query(client, index);
                                    latencies[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                                    Assert.AreEqual(expectedCount, result.Value.GetProperty(profile == DatabaseProfileId.Rdf ? "bindings" : "entities").GetArrayLength());
                                }
                            }));
                            watch.Stop();
                            double cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                            long allocatedBytes = GC.GetTotalAllocatedBytes() - allocated;
                            Array.Sort(latencies);
                            var probe = await Query(client, 0);
                            results.Add(new
                            {
                                Profile = profile.ToString(),
                                Shape = shape,
                                Concurrency = concurrency,
                                Repetition = repetition,
                                Compact = compact,
                                ShardServing = shardServing,
                                TimestampReadAdmission = "independent",
                                Count = count,
                                Seconds = watch.Elapsed.TotalSeconds,
                                PerSecond = count / watch.Elapsed.TotalSeconds,
                                ProcessCpuMillisecondsPerRead = cpuMilliseconds / count,
                                ProcessAllocatedBytesPerRead = allocatedBytes / (double)count,
                                P95Milliseconds = latencies[(int)Math.Ceiling(count * .95) - 1],
                                Statistics = probe.Statistics,
                                SamplesMilliseconds = latencies
                            });
                            Directory.CreateDirectory(directory);
                            var assemblies = new[]
                            {
                                typeof(PartitionDatabaseGateway).Assembly, typeof(EntityForge.Db.Rdf.RdfSparql).Assembly,
                        typeof(EntityForge.Db.Query.PortableEntityQueryExecutor).Assembly, typeof(EntityForge.Db.Kernel.DatabaseKernel).Assembly,
                        typeof(EntityForge.Db.Semantic.Engine.EntityForgeWriteTransaction).Assembly, GetType().Assembly
                            }
                                .Select(a => new { Path = a.Location, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).ToArray();
                            await File.WriteAllTextAsync(Path.Combine(directory, profile + ".json"), JsonSerializer.Serialize(new
                            {
                                Assemblies = assemblies,
                                Results = results
                            }, new JsonSerializerOptions { WriteIndented = true }));
                        }
                    }
                }
            }
        }, bothCompact: shardServing);
    }
}
