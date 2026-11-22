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
    public async Task Execution_shaped_profile_projection_measurement(DatabaseProfileId profile)
    {
        var directory = Environment.GetEnvironmentVariable("ENTITYFORGE_EXECUTION_RESPONSE_MEASURE");
        if (string.IsNullOrWhiteSpace(directory))
        {
            Assert.Inconclusive("Opt-in response measurement.");
            return;
        }
        const int entities = 32, repetitions = 3, warmups = 32, serialSamples = 128, concurrentSamples = 2048;
        var results = new List<object>();
        await WithProfileFragments(profile, async (_, candidate, _, client, http, _, _) =>
        {
            string? concept = null, property = null;
            if (profile == DatabaseProfileId.Rdf)
            {
                await candidate.UpdateQueryAsync(new(candidate.Generation, profile, EntityOperationIds.New(), "sparql",
                    "INSERT DATA { " + string.Join(" ", Enumerable.Range(0, entities).Select(i =>
                        $"<urn:item:{i}> <urn:name> \"value-{i % 4}\" .")) + " }"), default);
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
            }
            ValueTask<PartitionQueryResult> Query() => profile == DatabaseProfileId.Rdf
                ? client.QuerySparqlAsync("SELECT ?s ?name WHERE { ?s <urn:name> ?name FILTER (?name = \"value-2\") } ORDER BY ?s")
                : client.QuerySemanticAsync(new(concept!, new(concept!, Fields: [new(property!, "name")]),
                    Filter: new("propertycomparison", property, "equal", new(JsonSerializer.SerializeToElement("value-2"))), ConceptMatchMode: "direct"));
            string field = profile == DatabaseProfileId.Rdf ? "bindings" : "entities";
            var expected = await Query();
            Assert.AreEqual(entities / 4, expected.Value.GetProperty(field).GetArrayLength());
            foreach (int concurrency in new[] { 1, 32 })
            {
                for (int repetition = 0; repetition < repetitions; repetition++)
                {
                    for (int i = 0; i < warmups; i++)
                    {
                        await Query();
                    }

                    int count = concurrency == 1 ? serialSamples : concurrentSamples;
                    using var process = Process.GetCurrentProcess();
                    var cpu = process.TotalProcessorTime;
                    long allocated = GC.GetTotalAllocatedBytes();
                    int next = -1;
                    var latencies = new double[count];
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
                            var response = await Query();
                            Assert.AreEqual(entities / 4, response.Value.GetProperty(field).GetArrayLength());
                            latencies[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        }
                    }));
                    watch.Stop();
                    Array.Sort(latencies);
                    results.Add(new
                    {
                        Profile = profile.ToString(),
                        Concurrency = concurrency,
                        Repetition = repetition,
                        Count = count,
                        PerSecond = count / watch.Elapsed.TotalSeconds,
                        P95Milliseconds = latencies[(int)Math.Ceiling(count * .95) - 1],
                        ProcessCpuMillisecondsPerRead = (process.TotalProcessorTime - cpu).TotalMilliseconds / count,
                        ProcessAllocatedBytesPerRead = (GC.GetTotalAllocatedBytes() - allocated) / (double)count
                    });
                }
            }

            var assemblies = new[]
            {
                typeof(PartitionDatabaseGateway).Assembly, typeof(EntityForge.Db.Rdf.RdfSparql).Assembly,
                typeof(EntityForge.Db.Query.PortableEntityQueryExecutor).Assembly, typeof(EntityForge.Db.Kernel.DatabaseKernel).Assembly, GetType().Assembly
            }
                .Select(a => new { a.Location, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) });
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, profile + ".json"), JsonSerializer.Serialize(new
            {
                Population = "same-process HTTPS diagnostic; client and two participants share measured process; no fixed CPU allowance",
                Assemblies = assemblies,
                Results = results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }, bothCompact: true);
    }
}
