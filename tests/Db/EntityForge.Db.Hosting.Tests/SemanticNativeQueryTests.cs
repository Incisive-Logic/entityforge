using EntityForge.Db.Semantic.Query;
using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Hosting;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Semantics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SemanticNativeQueryTests
{
    private static readonly SemanticTerm Person = new(new Uri("urn:test:Person"), SemanticTermRole.Concept);
    private static readonly SemanticTerm Name = new(new Uri("urn:test:name"), SemanticTermRole.Property);
    private static readonly SemanticEntityIdentity Alice = new(Person,
        new SemanticIdentityScheme(new Uri("urn:test:id"), "id", SemanticValueKind.String), new StringSemanticValue("alice"));

    private static SemanticProvenance Provenance(string source) => new(new Uri("urn:test:" + source), "record",
        DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        new SemanticModelReference(new Uri("urn:test:vocabulary"), "1", SemanticModelKind.Vocabulary));

    private static SemanticFieldEvidence Field(SemanticValue value, string source = "a") =>
        new(Guid.NewGuid(), Alice, Name, value, Provenance(source), new(1), null);

    [TestMethod]
    public async Task View_identities_match_stateless_SHA256_across_values_and_parallel_construction()
    {
        var fields = Enumerable.Range(0, 12).Select(i => Field(new StringSemanticValue("value-" + i))).ToArray();
        static Guid Expected(string value) => new(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
        var atom = new SemanticCanonicalAtomCodec().Encode(Alice);
        string entity = "urn:entityforge:semantic:entity:atom" + SemanticCanonicalAtomCodec.FormatVersion + ":" +
            Convert.ToHexStringLower(atom.FullHash.Span);
        var expectedVertices = fields.Select(field => Expected("value:" + field.AssertionId.ToString("D")))
            .Append(Expected(entity)).ToArray();
        var expectedEdges = fields.Select(field => Expected("assertion:" + field.AssertionId.ToString("D"))).ToArray();
        var views = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => SemanticNativeQueryView.Create(fields, [], default))));
        foreach (var view in views)
        {
            CollectionAssert.AreEquivalent(expectedVertices, view.Graph.Vertices.Keys.Select(id => id.Value).ToArray());
            CollectionAssert.AreEquivalent(expectedEdges, view.Graph.Edges.Keys.Select(id => id.Value).ToArray());
        }
    }

    [TestMethod]
    public void Shared_and_reconstructed_evidence_have_identical_views_and_payload_charges()
    {
        var provenance = Provenance("shared");
        var fields = Enumerable.Range(0, 8).Select(i => Field(new StringSemanticValue("value-" + i)) with { Provenance = provenance }).ToArray();
        var shared = SemanticNativeQueryView.Create(fields, [], default);
        var reconstructed = SemanticNativeQueryView.Create(fields.Select(field => field with
        {
            Subject = field.Subject with
            {
            },
            Provenance = field.Provenance with
            {
            }
        }).ToArray(), [], default);
        CollectionAssert.AreEquivalent(shared.Quads.ToArray(), reconstructed.Quads.ToArray());
        CollectionAssert.AreEquivalent(shared.Graph.Vertices.Keys.ToArray(), reconstructed.Graph.Vertices.Keys.ToArray());
        CollectionAssert.AreEquivalent(shared.Graph.Edges.Keys.ToArray(), reconstructed.Graph.Edges.Keys.ToArray());
        Assert.AreEqual(shared.PayloadBytes, reconstructed.PayloadBytes);
    }

    [TestMethod]
    public async Task Current_native_view_remains_readable_during_concurrent_publication()
    {
        string path = Path.Combine(Path.GetTempPath(), "entityforge-concurrent-view-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await BenchmarkDatabase.OpenAsync(path, DatabaseProfileId.Semantic, "embedded-api", default);
            var database = host.Runtime!.Database;
            var store = database.EntityStore<BenchmarkItem>();
            await store.SaveAsync(new("one", "value-0"));
            var (initial, initialVersion) = await SemanticNativeQueryEndpoints.CaptureAsync(database, default);
            var initialQuads = initial.Quads.ToArray();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await Task.WhenAll(Task.Run(async () =>
            {
                for (int i = 1; i <= 80; i++)
                {
                    await store.SaveAsync(new("one", "value-" + i));
                }
            }), Task.Run(async () =>
            {
                var previous = initialVersion;
                for (int i = 0; i < 160; i++)
                {
                    var (view, version) = await SemanticNativeQueryEndpoints.CaptureAsync(database, timeout.Token);
                    Assert.IsTrue(version.Value >= previous.Value);
                    Assert.AreEqual(1, view.Quads.Count(q => q.Graph is null &&
                        q.Object is RdfLiteral literal && literal.LexicalForm.StartsWith("value-", StringComparison.Ordinal)));
                    previous = version;
                }
            }));
            CollectionAssert.AreEquivalent(initialQuads, initial.Quads.ToArray());
            var (final, _) = await SemanticNativeQueryEndpoints.CaptureAsync(database, timeout.Token);
            Assert.IsTrue(final.Quads.Any(q => q.Graph is null && q.Object is RdfLiteral { LexicalForm: "value-80" }));
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [TestMethod]
    public async Task Thousand_entity_view_is_reused_without_reusing_state_after_a_write()
    {
        string path = Path.Combine(Path.GetTempPath(), "entityforge-semantic-view-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await BenchmarkDatabase.OpenAsync(path, DatabaseProfileId.Semantic, "embedded-api", default);
            var database = host.Runtime!.Database;
            var store = database.EntityStore<BenchmarkItem>();
            for (int i = 0; i < 1000; i++)
            {
                await store.SaveAsync(new(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "initial-" + i));
            }
            var (before, version) = await SemanticNativeQueryEndpoints.CaptureAsync(database, default);
            Console.WriteLine($"Accounted projection payload: {before.PayloadBytes} bytes.");
            var (repeated, repeatedVersion) = await SemanticNativeQueryEndpoints.CaptureAsync(database, default);
            Assert.AreEqual(version, repeatedVersion);
            Assert.AreSame(before, repeated);
            await store.SaveAsync(new("0", "changed"));
            var (after, changedVersion) = await SemanticNativeQueryEndpoints.CaptureAsync(database, default);
            Assert.IsTrue(changedVersion > version);
            Assert.AreNotSame(before, after);
            Assert.IsFalse(before.Quads.Any(q => q.Object is RdfLiteral { LexicalForm: "changed" }));
            Assert.IsTrue(after.Quads.Any(q => q.Object is RdfLiteral { LexicalForm: "changed" }));
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [TestMethod]
    public async Task Conflicting_and_duplicate_assertions_preserve_provenance_and_gql_multiplicity()
    {
        var fields = new[]
        {
            Field(new StringSemanticValue("Alice")), Field(new StringSemanticValue("Alice"), "b"),
            Field(new StringSemanticValue("Alicia"), "c")
        };
        var view = SemanticNativeQueryView.Create(fields, [], default);
        Assert.AreEqual(2, view.Quads.Count(q => q.Graph is null && q.Predicate.Value == "urn:test:name"));
        Assert.AreEqual(3, view.Quads.Count(q => q.Graph is RdfIri graph && graph.Value.StartsWith(SemanticNativeQueryView.Vocabulary + "assertion:", StringComparison.Ordinal)));
        var sparql = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation(view.Quads,
            "SELECT ?name WHERE { ?s <urn:test:name> ?name } ORDER BY ?name", default);
        Assert.AreEqual(2, sparql.Bindings.Count);
        var compiled = new GraphGqlQueryCompiler().CompileStandard(new(
            "MATCH (p:`urn:test:Person`)-[a:`urn:test:name`]->(v) RETURN v.scalar AS name ORDER BY name"));
        var result = await SemanticNativeQueryEndpoints.GqlAsync(view, new(1), compiled, false, default);
        var json = JsonSerializer.Serialize(GraphPatternQueryResponseMapper.Project(result));
        StringAssert.Contains(json, "Alice");
        StringAssert.Contains(json, "Alicia");
        Assert.AreEqual(3, view.Graph.Edges.Count);
    }

    [TestMethod]
    public void Typed_literals_null_and_identity_schemes_do_not_collapse()
    {
        var other = new SemanticEntityIdentity(Person,
            new SemanticIdentityScheme(new Uri("urn:test:other-id"), "other", SemanticValueKind.String), Alice.Value);
        var view = SemanticNativeQueryView.Create([
            Field(new LanguageStringSemanticValue("hello", "en")),
            Field(new TypedLiteralSemanticValue("01", new Uri("urn:test:custom"))) with { Subject = other },
            Field(new NullSemanticValue())], [], default);
        Assert.AreEqual(2, view.Quads.Where(q => q.Graph is null && q.Predicate.Value == "urn:test:name").Select(q => q.Subject).Distinct().Count());
        Assert.IsTrue(view.Quads.Any(q => q.Object is RdfLiteral { Language: "en" }));
        Assert.IsTrue(view.Quads.Any(q => q.Object is RdfLiteral { LexicalForm: "01", Datatype: "urn:test:custom" }));
        Assert.IsTrue(view.Quads.Any(q => q.Object is RdfLiteral { Datatype: SemanticNativeQueryView.Vocabulary + "Null" }));
    }

    [TestMethod]
    public void Projection_enforces_cancellation_and_evidence_bound()
    {
        var field = Field(new StringSemanticValue("x"));
        Assert.ThrowsException<OperationCanceledException>(() => SemanticNativeQueryView.Create([field], [], new(true)));
        Assert.ThrowsException<NotSupportedException>(() => SemanticNativeQueryView.Create(
            Enumerable.Repeat(field, SemanticNativeQueryView.MaximumEvidence + 1).ToArray(), [], default));
        Assert.ThrowsException<NotSupportedException>(() => SemanticNativeQueryView.Create(
            [Field(new StringSemanticValue(new string('x', (int)SemanticNativeQueryView.MaximumBytes + 1)))], [], default));
    }

    [TestMethod]
    public async Task Relationship_traversal_joins_numeric_field_assertions_in_both_languages()
    {
        var bob = new SemanticEntityIdentity(Person, Alice.Scheme, new StringSemanticValue("bob"));
        var knows = new SemanticTerm(new Uri("urn:test:knows"), SemanticTermRole.Relationship);
        var age = new SemanticTerm(new Uri("urn:test:age"), SemanticTermRole.Property);
        var view = SemanticNativeQueryView.Create([Field(new IntegerSemanticValue(42)) with { Subject = bob, Field = age }],
            [new(Guid.NewGuid(), Alice, knows, bob, Provenance("a"), new(1), null)], default);
        var sparql = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation(view.Quads,
            "SELECT ?age WHERE { ?a <urn:test:knows>/<urn:test:age> ?age FILTER(?age > 40) }", default);
        Assert.AreEqual(1, sparql.Bindings.Count);
        Assert.AreEqual("42", ((RdfLiteral)sparql.Bindings[0]["age"]).LexicalForm);
        var query = new GraphGqlQueryCompiler().CompileStandard(new(
            "MATCH (a)-[:`urn:test:knows`]->(b)-[:`urn:test:age`]->(v) WHERE v.scalar > 40 RETURN v.scalar AS age"));
        var result = await SemanticNativeQueryEndpoints.GqlAsync(view, new(1), query, false, default);
        StringAssert.Contains(JsonSerializer.Serialize(GraphPatternQueryResponseMapper.Project(result)), "42");
    }

    [TestMethod]
    public async Task Every_semantic_value_kind_has_an_explicit_projection()
    {
        SemanticValue[] values =
        [
            new NullSemanticValue(), new BooleanSemanticValue(true), new IntegerSemanticValue(long.MinValue),
            new FloatingPointSemanticValue(double.NaN), new DecimalSemanticValue(1.25m), new StringSemanticValue("text"),
            new LanguageStringSemanticValue("hello", "en"), new DateSemanticValue(new(2026, 1, 1)),
            new TimeSemanticValue(new(12, 30)), new DateTimeSemanticValue(DateTimeOffset.Parse("2026-01-01T12:00:00+02:00")),
            new DurationSemanticValue(TimeSpan.FromTicks(-1)), new BinarySemanticValue(new byte[] { 0, 255 }),
            new AbsoluteUriSemanticValue(new("urn:test:value")), new GuidSemanticValue(Guid.Empty),
            new JsonSemanticValue("{\"a\":1}"), new TypedLiteralSemanticValue("lexical", new("urn:test:datatype"))
        ];
        Assert.AreEqual(Enum.GetValues<SemanticValueKind>().Length, values.Length);
        var view = SemanticNativeQueryView.Create(values.Select(v => Field(v)).ToArray(), [], default);
        Assert.AreEqual(values.Length, view.Graph.Edges.Count);
        Assert.AreEqual(values.Length, view.Quads.Count(q => q.Graph is null && q.Predicate.Value == Name.Identifier.AbsoluteUri));
        Assert.AreEqual(new RdfLiteral("NaN", SemanticNativeQueryView.Xsd + "double"), SemanticNativeQueryView.Term(values[3]));
        Assert.AreEqual(10, view.Graph.Vertices.Values.Count(v => v.Labels.Contains(SemanticNativeQueryView.Vocabulary + "Value") && v.Properties.ContainsKey("scalar")));
        var duration = view.Graph.Vertices.Values.SelectMany(v => v.Properties.Values).OfType<DatabaseValue.Duration>().Single();
        Assert.AreEqual(-1L, duration.Value.Seconds);
        Assert.AreEqual(999_999_900, duration.Value.Nanoseconds);
        var query = new GraphGqlQueryCompiler().CompileStandard(new(
            "MATCH (v) WHERE v.scalar = DATE '2026-01-01' RETURN v.scalar AS observedDate"));
        var result = await SemanticNativeQueryEndpoints.GqlAsync(view, new(1), query, false, default);
        StringAssert.Contains(JsonSerializer.Serialize(GraphPatternQueryResponseMapper.Project(result)), "2026-01-01");
    }

    [TestMethod]
    public void Reconstructed_identities_share_the_same_durable_view_vertex()
    {
        var scheme = new SemanticIdentityScheme(new("urn:test:string-id"), "string", SemanticValueKind.String);
        var first = new SemanticEntityIdentity(Person, scheme, new StringSemanticValue("identity"));
        var reconstructed = new SemanticEntityIdentity(Person, scheme, new StringSemanticValue("identity"));
        var view = SemanticNativeQueryView.Create([
            Field(new StringSemanticValue("first")) with { Subject = first },
            Field(new StringSemanticValue("second")) with { Subject = reconstructed }], [], default);
        Assert.AreEqual(1, view.Graph.Vertices.Values.Count(v => v.Labels.Contains(Person.Identifier.AbsoluteUri)));
        Assert.AreEqual(2, view.Graph.Edges.Count);
        Assert.AreEqual(1, view.Quads.Where(q => q.Graph is null && q.Predicate.Value == Name.Identifier.AbsoluteUri).Select(q => q.Subject).Distinct().Count());
    }

    [TestMethod]
    public async Task Hosted_queries_read_replaced_and_reopened_physical_semantic_state_with_read_credentials()
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-semantic-native-" + Guid.NewGuid().ToString("N"));
        const string reader = "semantic-native-reader-token-000001";
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(path)
                    .UseBearerTokenSecurity(new("semantic-native-admin-token-000001", "semantic-native-writer-token-00001", reader, requireHttps: false))
                    .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
                await host.StartAsync();
                var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
                if (pass == 0)
                {
                    var (empty, _) = await SemanticNativeQueryEndpoints.CaptureAsync(runtime.Database, default);
                    Assert.AreEqual(0, empty.Quads.Count);
                    await runtime.Database.EntityStore<EntityOperationTransportTests.Item>().SaveAsync(new("one", "first"));
                }
                using var http = new HttpClient
                {
                    BaseAddress = new(host.Application.Services.GetRequiredService<IServer>()
                        .Features.Get<IServerAddressesFeature>()!.Addresses.Single())
                };
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync("/sparql?query=ASK%20%7B%7D")).StatusCode);
                http.DefaultRequestHeaders.Authorization = new("Bearer", reader);
                var capabilities = await http.GetStringAsync("/v1/capabilities");
                StringAssert.Contains(capabilities, "semantic-gql");
                StringAssert.Contains(capabilities, "semantic-sparql");
                var nativeSparql = await http.PostAsJsonAsync("/v1/semantic/sparql", new
                {
                    query = "ASK { ?s ?p ?o }"
                });
                Assert.AreEqual(HttpStatusCode.OK, nativeSparql.StatusCode, await nativeSparql.Content.ReadAsStringAsync());
                var historical = await http.PostAsJsonAsync("/v1/semantic/gql/query", new
                {
                    query = "RETURN 1 AS n",
                    snapshotVersion = runtime.Database.CurrentVersion.Value
                });
                Assert.IsFalse(historical.IsSuccessStatusCode);
                var description = await http.GetStringAsync("/sparql");
                Assert.IsFalse(description.Contains("/sparql/update", StringComparison.Ordinal));
                string expected = pass == 0 ? "first" : "later";
                var response = await http.GetAsync("/sparql?query=" + Uri.EscapeDataString("SELECT ?value WHERE { ?s ?p ?value FILTER(?value = \"" + expected + "\") }"));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                StringAssert.Contains(await response.Content.ReadAsStringAsync(), expected);
                var gql = await http.PostAsJsonAsync("/v1/semantic/gql/query", new
                {
                    query = "MATCH (v) WHERE v.scalar = '" + expected + "' RETURN v.scalar AS name"
                });
                Assert.AreEqual(HttpStatusCode.OK, gql.StatusCode, await gql.Content.ReadAsStringAsync());
                StringAssert.Contains(await gql.Content.ReadAsStringAsync(), expected);
                var rejected = await http.PostAsJsonAsync("/v1/semantic/gql/query", new
                {
                    query = "INSERT (:Bad)"
                });
                Assert.IsFalse(rejected.IsSuccessStatusCode);
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.PostAsync("/sparql/update", new StringContent("CLEAR ALL"))).StatusCode);
                http.DefaultRequestHeaders.Authorization = new("Bearer", "semantic-native-admin-token-000001");
                Assert.AreEqual(HttpStatusCode.NotFound, (await http.PostAsync("/sparql/update", new StringContent("CLEAR ALL"))).StatusCode);
                http.DefaultRequestHeaders.Authorization = new("Bearer", reader);
                if (pass == 0)
                {
                    var (before, _) = await SemanticNativeQueryEndpoints.CaptureAsync(runtime.Database, default);
                    var repeated = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
                        (await SemanticNativeQueryEndpoints.CaptureAsync(runtime.Database, default)).View));
                    foreach (var cached in repeated)
                    {
                        Assert.AreSame(before, cached);
                    }
                    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                        await SemanticNativeQueryEndpoints.CaptureAsync(runtime.Database, new(true)));
                    await runtime.Database.EntityStore<EntityOperationTransportTests.Item>().SaveAsync(new("one", "later"));
                    var (after, _) = await SemanticNativeQueryEndpoints.CaptureAsync(runtime.Database, default);
                    Assert.AreNotSame(before, after);
                    Assert.IsTrue(before.Quads.Any(q => q.Graph is null && q.Object is RdfLiteral { LexicalForm: "first" }));
                    Assert.IsFalse(after.Quads.Any(q => q.Graph is null && q.Object is RdfLiteral { LexicalForm: "first" }));
                    Assert.IsTrue(after.Quads.Any(q => q.Graph is null && q.Object is RdfLiteral { LexicalForm: "later" }));
                    await runtime.Database.ReindexAsync();
                    var (reindexed, _) = await SemanticNativeQueryEndpoints.CaptureAsync(runtime.Database, default);
                    CollectionAssert.AreEquivalent(after.Quads.ToArray(), reindexed.Quads.ToArray());
                }
                else
                {
                    await runtime.Database.EntityStore<EntityOperationTransportTests.Item>().DeleteAsync("one");
                    var (deleted, _) = await SemanticNativeQueryEndpoints.CaptureAsync(runtime.Database, default);
                    Assert.AreEqual(0, deleted.Quads.Count);
                }
            }
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }
}
