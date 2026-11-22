using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    // Deliberately bounded, opt-in public-API screen; not a correctness-suite benchmark.
    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Limited_public_profile_comparison(DatabaseProfileId profile)
    {
        string? output = Environment.GetEnvironmentVariable("ENTITYFORGE_LIMITED_PROFILE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
        {
            Assert.Inconclusive("Opt-in profile comparison.");
            return;
        }
        string? selectedProfile = Environment.GetEnvironmentVariable("ENTITYFORGE_LIMITED_PROFILE");
        if (selectedProfile is not null && selectedProfile != profile.ToString())
        {
            Assert.Inconclusive("Another profile selected.");
            return;
        }
        const int records = 64, readSamples = 2048, writeSamples = 64, readWindows = 3, writeWindows = 2;
        var results = new List<object>();
        var graphMap = GraphEntityMap.For<Item>().VertexLabel("ProfileItem").Identity(x => x.Id, "id")
            .Property(x => x.Name, "name").Materialize(c => new(c.IdentityAs<string>(), c.Required<string>("Name")));
        await WithProfileFragments(profile, async (_, candidate, _, client, http, nodes, _) =>
        {
            var expected = new ConcurrentDictionary<string, string>();
            async Task Save(int id, string name)
            {
                var item = new Item(id.ToString(), name);
                if (profile == DatabaseProfileId.PropertyGraph)
                {
                    await client.SaveGraphAsync(item, graphMap);
                }
                else if (profile == DatabaseProfileId.Rdf)
                {
                    await client.SaveRdfAsync(item);
                }
                else
                {
                    await client.SaveSemanticAsync(item);
                }

                expected[item.Id] = name;
            }
            async Task<Item> Read(int id)
            {
                Item? item = profile switch
                {
                    DatabaseProfileId.PropertyGraph => (await client.ReadGraphAsync(id.ToString(), graphMap)).Value,
                    DatabaseProfileId.Rdf => (await client.ReadRdfAsync<Item>(id.ToString())).Value,
                    _ => (await client.ReadSemanticAsync<Item>(id.ToString())).Value
                };
                Assert.IsNotNull(item);
                Assert.AreEqual(id.ToString(), item.Id);
                Assert.AreEqual(expected[item.Id], item.Name);
                return item;
            }
            for (int id = 0; id < records; id++)
            {
                await Save(id, "value-" + id);
            }

            var manifest = profile switch
            {
                DatabaseProfileId.PropertyGraph => new HttpGraphEntityClient(http, candidate.Generation).EntityStore(graphMap).Mapping.Manifest,
                DatabaseProfileId.Rdf => new HttpRdfEntityClient(http, candidate.Generation).EntityStore<Item>().Mapping.Manifest,
                _ => new HttpSemanticEntityClient(http, candidate.Generation).EntityStore<Item>().Manifests[0]
            };
            string nameProperty = manifest.ProfileBindings.Single(b => b.MemberIdentity ==
                manifest.Members.Single(m => m.ClrName == "Name").Identity).StoredBinding;
            string type = profile == DatabaseProfileId.PropertyGraph ? "ProfileItem" : manifest.ProfileTypeBinding;
            var examples = new ConcurrentDictionary<string, object>();
            async Task Query(int id, bool scan)
            {
                string value = expected[id.ToString()];
                PartitionQueryResult result;
                if (profile == DatabaseProfileId.PropertyGraph)
                {
                    string statement = $"MATCH (n:{type})" + (scan ? "" : $" WHERE n.{nameProperty} = {JsonSerializer.Serialize(value)}") + $" RETURN n.{nameProperty} AS name";
                    examples[scan ? "scan" : "point"] = statement;
                    result = await client.QueryGqlAsync(statement);
                }
                else if (profile == DatabaseProfileId.Rdf)
                {
                    string statement = $"SELECT ?name WHERE {{ ?s a <{type}> . ?s <{nameProperty}> ?name ." +
                        (scan ? "" : $" FILTER(?name = {JsonSerializer.Serialize(value)})") + " }";
                    examples[scan ? "scan" : "point"] = statement;
                    result = await client.QuerySparqlAsync(statement);
                }
                else
                {
                    var request = new PortableEntityQueryRequest(type, new(type, Fields: [new(nameProperty, "name")]),
                        Filter: scan ? null : new("propertyComparison", nameProperty, "equal", new(JsonSerializer.SerializeToElement(value))),
                        ConceptMatchMode: "direct");
                    examples[scan ? "scan" : "point"] = request;
                    result = await client.QuerySemanticAsync(request);
                }
                int count = result.Value.GetProperty(profile == DatabaseProfileId.Semantic ? "entities" :
                    profile == DatabaseProfileId.PropertyGraph ? "matches" : "bindings").GetArrayLength();
                Assert.AreEqual(scan ? records : 1, count);
                if (scan)
                {
                    foreach (string name in expected.Values)
                    {
                        StringAssert.Contains(result.Value.GetRawText(), name);
                    }
                }
                else
                {
                    StringAssert.Contains(result.Value.GetRawText(), value);
                }
            }
            async Task Measure(string workload, int concurrency, int samples, int windows, Func<int, int, Task> operation)
            {
                for (int window = 0; window < windows; window++)
                {
                    var elapsed = new double[samples];
                    var errors = new ConcurrentBag<string>();
                    long allocation = GC.GetTotalAllocatedBytes(true);
                    var started = DateTimeOffset.UtcNow;
                    var watch = Stopwatch.StartNew();
                    await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async worker =>
                    {
                        for (int i = worker; i < samples; i += concurrency)
                        {
                            long stamp = Stopwatch.GetTimestamp();
                            try
                            {
                                await operation(i, window);
                                elapsed[i] = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
                            }
                            catch (Exception failure)
                            {
                                errors.Add(failure.GetType().Name + ": " + failure.Message);
                                elapsed[i] = double.NaN;
                            }
                        }
                    }));
                    watch.Stop();
                    long allocated = GC.GetTotalAllocatedBytes(true) - allocation;
                    var successes = elapsed.Where(double.IsFinite).Order().ToArray();
                    results.Add(new
                    {
                        Workload = workload,
                        Concurrency = concurrency,
                        Window = window,
                        Selected = workload.Contains("read") || workload.Contains("query") && workload != "query-update" ? window > 0 : true,
                        Attempted = samples,
                        Succeeded = successes.Length,
                        Seconds = watch.Elapsed.TotalSeconds,
                        PerSecond = successes.Length / watch.Elapsed.TotalSeconds,
                        P95Ms = successes.Length == 0 ? (double?)null : successes[(int)Math.Ceiling(successes.Length * .95) - 1],
                        ProcessAllocatedBytesPerAttempt = allocated / samples,
                        StartedUtc = started,
                        Errors = errors.ToArray()
                    });
                }
            }
            // Fail before timing if a profile's actual query syntax/shape is unsupported.
            await Query(0, false);
            await Query(0, true);
            foreach (int c in new[] { 1, 32 })
            {
                await Measure("mapped-read", c, readSamples, readWindows, (i, _) => Read(i % records));
                await Measure("point-query", c, readSamples, readWindows, (i, _) => Query(i % records, false));
                // Result-size control: same 64 names, no recursive materialisation/evidence request.
                await Measure("scan-query", c, 128, readWindows, (_, _) => Query(0, true));
            }
            foreach (int c in new[] { 1, 32 })
            {
                await Measure("mapped-save", c, writeSamples, writeWindows,
                    (i, window) => Save(i % records, $"save-{c}-{window}-{i}"));
            }

            if (profile != DatabaseProfileId.Semantic)
            {
                await Measure("query-update", 1, 32, writeWindows, async (i, window) =>
                {
                    string before = expected[i.ToString()], after = $"query-{window}-{i}";
                    string statement = profile == DatabaseProfileId.PropertyGraph
                        ? $"MATCH (n:{type}) WHERE n.{nameProperty} = {JsonSerializer.Serialize(before)} SET n.{nameProperty} = {JsonSerializer.Serialize(after)}"
                        : $"DELETE {{ ?s <{nameProperty}> ?old }} INSERT {{ ?s <{nameProperty}> {JsonSerializer.Serialize(after)} }} WHERE {{ ?s a <{type}> . ?s <{nameProperty}> ?old . FILTER(?old = {JsonSerializer.Serialize(before)}) }}";
                    examples["update"] = statement;
                    if (profile == DatabaseProfileId.PropertyGraph)
                    {
                        await client.ExecuteGqlUpdateAsync(EntityOperationIds.New(), statement);
                    }
                    else
                    {
                        await client.ExecuteSparqlUpdateAsync(EntityOperationIds.New(), statement);
                    }

                    expected[i.ToString()] = after;
                });
            }
            for (int id = 0; id < records; id++)
            {
                await Read(id);
            }

            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, profile + ".json"), JsonSerializer.Serialize(new
            {
                Profile = profile.ToString(),
                Records = records,
                Shards = 2,
                QueryHome = "local-a/remote-TLS-b",
                Composition = "public HTTPS; same Windows process hosts client/home/two native owners; live etcd placement; file transaction authority",
                Clock = "shared-host qualification bounded UTC",
                QueryUpdates = profile == DatabaseProfileId.Semantic ? "unsupported" : "C1 coordinated",
                OwnerVersions = nodes.Select(n => n.Kernel.CurrentVersion).ToArray(),
                Examples = examples,
                Results = results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }, commands: true);
    }
}
