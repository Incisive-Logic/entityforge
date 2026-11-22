using System.Text.Json;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using Google.Protobuf;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public void Distributed_Gql_compilation_uses_standard_boundary_and_composition()
    {
        var type = typeof(PartitionQueryRequest).Assembly.GetType("EntityForge.Db.Distributed.Hosting.PartitionQueryCompilation")!;
        var compile = type.GetMethod("CompileGraph", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Assert.IsNotNull(compile.Invoke(null, ["RETURN 1 AS n NEXT RETURN n+1 AS answer"]));
        Assert.IsNotNull(compile.Invoke(null, ["MATCH (n) FINISH"]));
        foreach (string source in new[] { "WITH 1 AS n RETURN n", "UNWIND [1] AS n RETURN n", "RETURN 'a' =~ 'a' AS answer" })
        {
            var error = Assert.ThrowsExactly<System.Reflection.TargetInvocationException>(() => compile.Invoke(null, [source]));
            Assert.IsInstanceOfType<ArgumentException>(error.InnerException);
        }
    }

    private static int ProfileScreenReads => int.TryParse(Environment.GetEnvironmentVariable("ENTITYFORGE_PROFILE_SCREEN_READS"), out var value) && value is >= 64 and <= 8192 && value % 8 == 0 ? value : 64;

    [TestMethod]
    public async Task Read_mostly_compilation_arbitrates_misses_and_evicts_bounded_interpretations()
    {
        // Keep this assembly independent of Hosting's private compiled-plan type.
        var type = typeof(PartitionQueryRequest).Assembly.GetType("EntityForge.Db.Distributed.Hosting.PartitionQueryCompilation")!;
        var compile = type.GetMethod("CompileGraph", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        object Read(string query) => compile.Invoke(null, [query])!;
        string statement = "MATCH (n) RETURN n" + new string(' ', 350);
        var first = Read(statement);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => Read(statement))));
        Assert.IsTrue(results.All(result => ReferenceEquals(first, result)));
        for (int i = 0; i < 300; i++)
        {
            Read("MATCH (n) RETURN n" + new string(' ', i));
        }

        Assert.AreNotSame(first, Read(statement), "Bounded interpretations must be reclaimable; this is not an unbounded plan cache.");
    }

    [TestMethod]
    public async Task Semantic_interpretation_experiment_screen()
    {
        await WithProfileFragments(DatabaseProfileId.Semantic, async (_, candidate, _, _, http) =>
        {
            var store = new EntityForge.Db.Semantic.HttpSemanticEntityClient(http, candidate.Generation).EntityStore<Item>();
            for (int i = 0; i < 6; i++)
            {
                var capture = store.CaptureSave(new(i.ToString(), "value-" + i), EntityForge.Db.Semantic.SemanticEntityWriteOperation.Create());
                await candidate.DeliverAsync(capture.OperationId, capture.Export());
            }
            var manifest = store.Manifests[0];
            string property = manifest.ProfileBindings.Single(p => p.MemberIdentity == manifest.Members.Single(m => m.ClrName == "Name").Identity).StoredBinding;
            var query = new EntityForge.Db.Transport.Contracts.PortableEntityQueryRequest(manifest.ProfileTypeBinding,
                new(manifest.ProfileTypeBinding, Fields: [new(property, "name")]), ConceptMatchMode: "direct");
            var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "entity", EntityQuery: query);
            async Task Read() => Assert.AreEqual(6, (await candidate.QueryAsync(request, default)).Value.GetProperty("entities").GetArrayLength());
            for (int i = 0; i < 8; i++)
            {
                await Read();
            }

            foreach (int concurrency in new[] { 1, 8 })
            {
                for (int window = 0; window < 3; window++)
                {
                    long allocation = GC.GetTotalAllocatedBytes(true);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
{
    for (int i = 0; i < ProfileScreenReads / concurrency; i++)
    {
        await Read();
    }

}));
                    watch.Stop();
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        Experiment = 6,
                        Concurrency = concurrency,
                        Window = window,
                        Reads = ProfileScreenReads,
                        Seconds = watch.Elapsed.TotalSeconds,
                        ReadsPerSecond = ProfileScreenReads / watch.Elapsed.TotalSeconds,
                        AllocatedBytesPerRead = (GC.GetTotalAllocatedBytes(true) - allocation) / ProfileScreenReads
                    }));
                }
            }
        }, commands: int.TryParse(Environment.GetEnvironmentVariable("ENTITYFORGE_QUERY_EXPERIMENTS"), out var mask) && (mask & 256) != 0);
    }

    [TestMethod]
    public async Task Rdf_shared_query_experiment_screen()
    {
        await WithProfileFragments(DatabaseProfileId.Rdf, async (_, candidate, _, _, _) =>
        {
            await candidate.UpdateQueryAsync(new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "sparql",
                "INSERT DATA { " + string.Join(" ", Enumerable.Range(0, 6).Select(i => $"<urn:screen:{i}> <urn:name> \"value-{i}\" .")) + " }"), default);
            var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "sparql", "SELECT ?s ?n WHERE { ?s <urn:name> ?n }");
            async Task Read() => Assert.AreEqual(6, (await candidate.QueryAsync(request, default)).Value.GetProperty("bindings").GetArrayLength());
            for (int i = 0; i < 8; i++)
            {
                await Read();
            }

            foreach (int concurrency in new[] { 1, 8 })
            {
                for (int window = 0; window < 3; window++)
                {
                    long allocation = GC.GetTotalAllocatedBytes(true);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
{
    for (int i = 0; i < ProfileScreenReads / concurrency; i++)
    {
        await Read();
    }

}));
                    watch.Stop();
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        Experiment = "RdfCombined",
                        Concurrency = concurrency,
                        Window = window,
                        Reads = ProfileScreenReads,
                        Seconds = watch.Elapsed.TotalSeconds,
                        ReadsPerSecond = ProfileScreenReads / watch.Elapsed.TotalSeconds,
                        AllocatedBytesPerRead = (GC.GetTotalAllocatedBytes(true) - allocation) / ProfileScreenReads
                    }));
                }
            }
        }, commands: int.TryParse(Environment.GetEnvironmentVariable("ENTITYFORGE_QUERY_EXPERIMENTS"), out var mask) && (mask & 256) != 0);
    }

    [TestMethod]
    public void Optimized_identity_wire_preserves_the_existing_byte_order()
    {
        for (int i = 0; i < 1024; i++)
        {
            Guid id = Guid.NewGuid();
            CollectionAssert.AreEqual(id.ToByteArray(), PartitionQueryOperatorWire.Id(id).ToByteArray());
        }
    }

    [TestMethod]
    public void Cached_operator_wire_never_reuses_completion_control_and_preserves_all_profile_pages()
    {
        PartitionQueryRelation[] relations = [
            new(Vertices: [new(new(Guid.NewGuid()), new HashSet<string>(), new Dictionary<string, DatabaseValue>())]),
            new(RdfBindings: [new(0, new() { ["o"] = new RdfLiteral("quoted \" 日本", language: "ja") })]),
            new(Catalog: new([], [], []), SemanticRows: [])];
        foreach (var relation in relations)
        {
            var page = new PartitionQueryExchangePage(relation, null);
            var value = new PartitionProjectionValue(page, PartitionQueryJson.Options)
            {
                QueryPageBytes = PartitionProjectionValue.Measure(page, PartitionQueryLimits.ExchangePageBytes, PartitionQueryJson.Options),
                OperatorWirePage = PartitionQueryOperatorWire.EncodePage(page)
            };
            var before = value.OperatorWirePage.ToByteArray();
            var control = OperatorControl();
            for (int i = 0; i < 8; i++)
            {
                var fresh = control with
                {
                    Operation = Guid.NewGuid(),
                    Completed = i % 2 == 0
                };
                var (observed, result) = PartitionQueryOperatorWire.Decode(PartitionQueryOperatorWire.Encode(fresh, new(true, value, fresh.Completed)));
                Assert.AreEqual(fresh, observed);
                Assert.AreEqual(value.QueryPageBytes, result.Value!.QueryPageBytes);
            }
            CollectionAssert.AreEqual(before, value.OperatorWirePage.ToByteArray());
        }
    }

    [TestMethod]
    public void Counting_projection_buffer_preserves_escaping_and_exact_limit_failures()
    {
        object[] values = [new { value = new string('\n', 8192) }, new { value = "日本語\"\\" }, new { value = "" }];
        foreach (var value in values)
        {
            byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(value, PartitionParticipantWire.Json);
            Assert.AreEqual((long)encoded.Length, PartitionProjectionValue.Measure(value, encoded.Length));
            Assert.ThrowsExactly<InvalidOperationException>(() => PartitionProjectionValue.Measure(value, encoded.Length - 1));
        }
    }
}
