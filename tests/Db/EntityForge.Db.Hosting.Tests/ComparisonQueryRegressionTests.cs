using System.Collections.Immutable;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class ComparisonQueryRegressionTests
{
    [TestMethod]
    public void Selective_rdf_lookups_do_not_build_population_sized_partitions()
    {
        const int Population = 100_000;
        const int Lookups = 32;
        const long AllocationLimit = 1024 * 1024;
        var predicate = new RdfIri("urn:name");
        var subject = new RdfIri("urn:item:40");
        var source = Enumerable.Range(0, Population).Select(i => new RdfQuad(new RdfIri("urn:item:" + i),
            predicate, new RdfLiteral("name:" + i))).ToArray();
        // Exercise the production private immutable read view without a disk fixture.
        var type = typeof(RdfProfileRuntime).GetNestedType("RdfReadView", System.Reflection.BindingFlags.NonPublic)!;
        var build = type.GetMethod("Build")!;
        var candidates = type.GetMethods().Single(m => m.Name == "Candidates" && m.GetParameters().Length == 5);
        var apply = type.GetMethod("Apply")!;
        var original = build.Invoke(null, [source])!;
        var changed = new RdfQuad(subject, predicate, new RdfLiteral("updated"));
        var next = apply.Invoke(original, [new RdfMutation[] { new RetractRdfQuad(source[40]), new AssertRdfQuad(changed) }])!;
        foreach (var view in new[] { original, next })
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Lookups; i++)
            {
                var result = (IReadOnlySet<RdfQuad>)candidates.Invoke(view, [subject, predicate, null, null, true])!;
                Assert.AreEqual(1, result.Count);
                Assert.AreEqual(ReferenceEquals(view, original) ? source[40] : changed, result.Single());
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.IsTrue(allocated < AllocationLimit, $"Selective lookups built a population-sized index: {allocated} bytes.");
        }
    }

    [TestMethod]
    public void Integer_candidates_seek_original_population_and_retain_noninteger_residuals()
    {
        const int Count = 100_000;
        const string Integer = "http://www.w3.org/2001/XMLSchema#integer";
        var predicate = new RdfIri("urn:number");
        var source = Enumerable.Range(0, Count).Select(i => new RdfQuad(new RdfIri("urn:item:" + i), predicate,
            new RdfLiteral(i.ToString(), Integer))).ToImmutableHashSet();
        var index = new RdfSparql.IntegerCandidates();
        _ = index.Select(source, [("=", 40)], () =>
{

}).ToArray();
        int work = 0;
        var selected = index.Select(source, [(">=", 40), ("<", 43)], () => work++).ToArray();
        CollectionAssert.AreEquivalent(new[] { "40", "41", "42" }, selected.Select(q => ((RdfLiteral)q.Object).LexicalForm).ToArray());
        Assert.IsTrue(work < 20, "A warm integer window must not revisit all 100000 candidates.");
        Assert.AreEqual(0, index.Select(source, [(">", 43), ("<", 40)], () =>
{

}).Count());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => index.Select(source, [("=", 40)], cancel.Token.ThrowIfCancellationRequested).ToArray());
        Assert.AreEqual(1, index.Select(source, [("=", 40)], () =>
{

}).Count());
        var extras = new[]
        {
            new RdfLiteral("40.5", "http://www.w3.org/2001/XMLSchema#decimal"),
            new RdfLiteral("4.1e1", "http://www.w3.org/2001/XMLSchema#double"), new RdfLiteral("invalid", Integer), new RdfLiteral("40")
        };
        var mixed = source.Where(q => int.Parse(((RdfLiteral)q.Object).LexicalForm) < 100)
            .Concat(extras.Select((literal, i) => new RdfQuad(new RdfIri("urn:extra:" + i), predicate, literal))).ToArray();
        foreach (string condition in new[] { "?n >= 40 && ?n < 43", "?n = 40" })
        {
            string oracle = condition.Contains("&&") ? "40 <= ?n && 43 > ?n" : "40 = ?n";
            RdfSparqlSelectResult Query(string filter) => (RdfSparqlSelectResult)RdfSparql.ExecuteRelation(mixed,
                "SELECT ?s ?n WHERE { ?s <urn:number> ?n . FILTER(" + filter + ") }", CancellationToken.None);
            CollectionAssert.AreEquivalent(Query(oracle).Bindings.Select(b => b["s"]).ToArray(), Query(condition).Bindings.Select(b => b["s"]).ToArray());
        }
    }

    [TestMethod]
    public void Graph_unrelated_updates_keep_ready_number_indexes_but_changed_columns_are_isolated()
    {
        const int Count = 100_000;
        var vertices = Enumerable.Range(0, Count).Select(i => new GraphVertex(new(Guid.NewGuid()), new HashSet<string> { "Item" },
            new Dictionary<string, DatabaseValue> { ["number"] = new DatabaseValue.Int64(i), ["name"] = new DatabaseValue.String("old") })).ToImmutableDictionary(v => v.Id);
        var before = GraphReadView.Build(vertices, ImmutableDictionary<GraphEdgeId, GraphEdge>.Empty);
        before.DeclareServingPropertyIndex("number");
        var numberRoot = before.RootIndexIdentity("number");
        var nameRoot = before.RootIndexIdentity("name");
        var vertex = vertices.Values.First();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var next = GraphProfileRuntime.ApplyServingEffects(before, [new GraphVertexPropertyChanged(vertex.Id, "name", new DatabaseValue.String("old"), new DatabaseValue.String("new"))]);
        for (int i = 0; i < 100; i++)
        {
            Assert.AreEqual(1, next.FindByProperty("number", new DatabaseValue.Int64(i)).Count);
        }

        Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - allocated < 1_000_000, "An unrelated update rebuilt/scanned the original-size number index.");
        Assert.AreSame(numberRoot, next.RootIndexIdentity("number"));
        Assert.AreNotSame(nameRoot, next.RootIndexIdentity("name"));
        var changed = GraphProfileRuntime.ApplyServingEffects(next, [new GraphVertexPropertyChanged(vertex.Id, "number", vertex.Properties["number"], new DatabaseValue.Int64(-1))]);
        Assert.AreNotSame(numberRoot, changed.RootIndexIdentity("number"));
        Assert.AreEqual(vertex.Id, changed.FindByProperty("number", new DatabaseValue.Int64(-1)).Single());
        Assert.AreEqual(0, before.FindByProperty("number", new DatabaseValue.Int64(-1)).Count);
        var deleted = GraphProfileRuntime.ApplyServingEffects(next, [new GraphVertexDeleted(vertex)]);
        Assert.AreEqual(0, deleted.FindByProperty("number", vertex.Properties["number"]).Count);
        Assert.AreEqual(1, next.FindByProperty("number", vertex.Properties["number"]).Count);
    }

    [TestMethod]
    public void Saturated_root_membership_stops_hashing_without_false_negatives()
    {
        int visited = 0;
        IEnumerable<string> Values()
        {
            for (int i = 0; i < 100_000; i++)
            {
                visited++;
                yield return i.ToString();
            }
        }
        var membership = DatabaseRootIndexMembership.Build(new("test", "number"), Values());
        Assert.IsTrue(visited < 100_000);
        Assert.IsTrue(Enumerable.Range(0, 100_000).All(i => membership.MayContain(i.ToString())));
        var sparse = DatabaseRootIndexMembership.Build(new("test", "number"), ["only"]);
        Assert.IsTrue(sparse.MayContain("only"));
        Assert.IsFalse(sparse.MayContain("missing"));
    }

    [TestMethod]
    public void Rdf_rejected_bindings_do_not_leak_into_siblings_optional_or_repeated_variables()
    {
        var predicate = new RdfIri("urn:number");
        var relation = Enumerable.Range(0, 100).Select(i => new RdfQuad(new RdfIri("urn:item:" + i), predicate,
            new RdfLiteral(i.ToString(), "http://www.w3.org/2001/XMLSchema#integer"))).ToArray();
        var result = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation(relation,
            "SELECT ?s ?n WHERE { ?s <urn:number> ?n . FILTER(?n >= 40 && ?n < 43) }", CancellationToken.None);
        Assert.AreEqual(3, result.Bindings.Count);
        CollectionAssert.AreEquivalent(new[] { "40", "41", "42" }, result.Bindings.Select(row => ((RdfLiteral)row["n"]).LexicalForm).ToArray());
        Assert.AreNotSame(result.Bindings[0], result.Bindings[1]);
        var repeated = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation(relation, "SELECT ?s WHERE { ?s <urn:number> ?s }", CancellationToken.None);
        Assert.AreEqual(0, repeated.Bindings.Count);
        var quoted = new[] { "a", "b" }.Select(id => new RdfQuad(
            new RdfTripleTerm(new RdfIri("urn:" + id), new RdfIri("urn:knows"), new RdfIri("urn:target")),
            new RdfIri("urn:source"), new RdfIri("urn:document"))).ToArray();
        var nested = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation(quoted,
            "SELECT ?s WHERE { << ?s <urn:knows> <urn:target> >> <urn:source> <urn:document> }", CancellationToken.None);
        Assert.AreEqual(2, nested.Bindings.Count, "Quoted bindings must reset between candidates too.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => RdfSparql.ExecuteRelation(relation,
            "SELECT ?s WHERE { ?s <urn:number> ?n }", cancelled.Token));
    }
}
