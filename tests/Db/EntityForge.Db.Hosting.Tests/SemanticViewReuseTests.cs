using EntityForge.Db.Semantic.Query;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SemanticViewReuseTests
{
    private static readonly SemanticTerm Concept = new(new Uri("urn:reuse:person"), SemanticTermRole.Concept);
    private static readonly SemanticTerm Name = new(new Uri("urn:reuse:name"), SemanticTermRole.Property);
    private static readonly SemanticIdentityScheme Scheme = new(new Uri("urn:reuse:id"), "id", SemanticValueKind.String);

    private static SemanticEntityIdentity Entity(string name) => new(Concept, Scheme, new StringSemanticValue(name));

    private static SemanticProvenance Provenance(string source) => new(new Uri("urn:reuse:" + source), "record", DateTimeOffset.UnixEpoch,
        new(new Uri("urn:reuse:model"), "1", SemanticModelKind.Vocabulary));

    private static SemanticFieldEvidence Field(string value) => new(Guid.NewGuid(), Entity("alice"), Name,
        new StringSemanticValue(value), Provenance("a"), new(1), null);

    private static void Equivalent(SemanticNativeQueryView expected, SemanticNativeQueryView actual)
    {
        Assert.AreEqual(expected.PayloadBytes, actual.PayloadBytes);
        CollectionAssert.AreEquivalent(expected.Quads.ToArray(), actual.Quads.ToArray());
        CollectionAssert.AreEquivalent(expected.Graph.Vertices.Keys.ToArray(), actual.Graph.Vertices.Keys.ToArray());
        CollectionAssert.AreEquivalent(expected.Graph.Edges.Keys.ToArray(), actual.Graph.Edges.Keys.ToArray());
        foreach (var pair in expected.Graph.Vertices)
        {
            var vertex = actual.Graph.Vertices[pair.Key];
            Assert.IsTrue(pair.Value.Labels.SetEquals(vertex.Labels));
            CollectionAssert.AreEquivalent(pair.Value.Properties.ToArray(), vertex.Properties.ToArray());
        }
        foreach (var pair in expected.Graph.Edges)
        {
            var edge = actual.Graph.Edges[pair.Key];
            Assert.AreEqual(pair.Value.From, edge.From);
            Assert.AreEqual(pair.Value.To, edge.To);
            Assert.AreEqual(pair.Value.Label, edge.Label);
            CollectionAssert.AreEquivalent(pair.Value.Properties.ToArray(), edge.Properties.ToArray());
        }
        const string query = "SELECT ?s ?p ?o WHERE { ?s ?p ?o } ORDER BY ?s ?p ?o";
        var scanned = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation(expected.Quads, query, default);
        using var dataset = actual.SparqlDataset(new(1));
        var indexed = (RdfSparqlSelectResult)RdfSparql.ExecuteDataset(dataset, query, default);
        Assert.AreEqual(scanned.Bindings.Count, indexed.Bindings.Count);
        for (int i = 0; i < scanned.Bindings.Count; i++)
        {
            CollectionAssert.AreEquivalent(scanned.Bindings[i].ToArray(), indexed.Bindings[i].ToArray());
        }
    }

    [TestMethod]
    public void Reused_views_equal_fresh_views_after_value_provenance_relationship_and_membership_changes()
    {
        var fields = new[] { Field("stable"), Field("duplicate"), Field("duplicate") };
        var relationship = new SemanticRelationshipEvidence(Guid.NewGuid(), Entity("alice"),
            new(new Uri("urn:reuse:knows"), SemanticTermRole.Relationship), Entity("bob"), Provenance("a"), new(1), null);
        var initial = SemanticNativeQueryView.Create(fields, [relationship], default);
        var same = SemanticNativeQueryView.Create(fields.Select(f => f with { Subject = Entity("alice"), Provenance = Provenance("a") }).ToArray(),
            [relationship with { Target = Entity("bob") }], default, initial);
        Equivalent(initial, same);
        foreach (var vertex in initial.Graph.Vertices)
        {
            Assert.AreSame(vertex.Value, same.Graph.Vertices[vertex.Key]);
        }
        var previous = same;
        var changed = new[] { fields[0], fields[1] with { Value = new StringSemanticValue("changed"), Provenance = Provenance("b"), BeginVersion = new(2) } };
        var self = relationship with
        {
            Target = Entity("alice"),
            Provenance = Provenance("b"),
            BeginVersion = new(2)
        };
        foreach (var input in new (SemanticFieldEvidence[] Fields, SemanticRelationshipEvidence[] Relationships)[]
        {
            (changed, [self]), (changed.Append(Field("new")).ToArray(), []), ([], []), (fields, [relationship])
        })
        {
            var oldQuads = previous.Quads.ToArray();
            var reused = SemanticNativeQueryView.Create(input.Fields, input.Relationships, default, previous);
            Equivalent(SemanticNativeQueryView.Create(input.Fields, input.Relationships, default), reused);
            CollectionAssert.AreEqual(oldQuads, previous.Quads.ToArray());
            previous = reused;
        }
    }

    [TestMethod]
    public void Reuse_keeps_payload_limits_and_cancellation()
    {
        string large = new('x', 1024 * 1024);
        var first = Field(large);
        var previous = SemanticNativeQueryView.Create([first], [], default);
        var oversized = Enumerable.Range(0, 20).Select(i => i == 0 ? first : Field(large)).ToArray();
        Assert.ThrowsExactly<NotSupportedException>(() => SemanticNativeQueryView.Create(oversized, [], default, previous));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => SemanticNativeQueryView.Create([first], [], cancelled.Token, previous));
        var reused = SemanticNativeQueryView.Create([first], [], default, previous);
        Assert.AreEqual(previous.PayloadBytes, reused.PayloadBytes);
        CollectionAssert.AreEquivalent(previous.Quads.ToArray(), reused.Quads.ToArray());
    }

    [TestMethod]
    public void Equal_numeric_or_temporal_values_do_not_hide_changed_lexical_metadata()
    {
        foreach (var pair in new (SemanticValue Before, SemanticValue After)[]
        {
            (new DecimalSemanticValue(1.0m), new DecimalSemanticValue(1.00m)),
            (new DateTimeSemanticValue(DateTimeOffset.UnixEpoch), new DateTimeSemanticValue(DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(1))))
        })
        {
            var field = Field("unused") with
            {
                Value = pair.Before
            };
            var previous = SemanticNativeQueryView.Create([field], [], default);
            var changed = field with
            {
                Value = pair.After
            };
            var fresh = SemanticNativeQueryView.Create([changed], [], default);
            var reused = SemanticNativeQueryView.Create([changed], [], default, previous);
            Equivalent(fresh, reused);
        }
    }

    [TestMethod]
    public async Task Warm_parallel_readers_share_only_the_exact_captured_state()
    {
        var directories = new TemporaryTestDirectories("entityforge-view-readers");
        try
        {
            await using var host = await BenchmarkDatabase.OpenAsync(directories.Create(), DatabaseProfileId.Semantic, "embedded-api", default);
            var database = host.Runtime!.Database;
            var store = database.EntityStore<BenchmarkItem>();
            await store.SaveAsync(new("one", "before"));
            var before = await SemanticNativeQueryEndpoints.CaptureAsync(database, default);
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            {
                for (int i = 0; i < 16; i++)
                {
                    var captured = await SemanticNativeQueryEndpoints.CaptureAsync(database, default);
                    Assert.AreSame(before.View, captured.View);
                    Assert.AreEqual(before.Version, captured.Version);
                }
            })));
            await store.SaveAsync(new("one", "after"));
            var captures = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
                await SemanticNativeQueryEndpoints.CaptureAsync(database, default))));
            foreach (var captured in captures)
            {
                Assert.IsTrue(captured.Version > before.Version);
                Assert.AreSame(captures[0].View, captured.View);
                Assert.IsTrue(captured.View.Quads.Any(q => q.Object is RdfLiteral { LexicalForm: "after" }));
            }
            Assert.IsFalse(before.View.Quads.Any(q => q.Object is RdfLiteral { LexicalForm: "after" }));
        }
        finally
        {
            directories.Cleanup();
        }
    }
}
