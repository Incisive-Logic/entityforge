using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SparqlDatasetAdapterTests
{
    [TestMethod]
    [DataRow("SELECT ?o { <urn:a> <urn:p>/<urn:p> ?o }")]
    [DataRow("SELECT ?o { GRAPH <urn:g> { <urn:a> <urn:p>/<urn:p> ?o } }")]
    public void Path_and_named_graph_evaluation_require_bounded_adapter_reads(string query)
    {
        var snapshot = new PrefixOnlySnapshot();
        var result = (RdfSparqlSelectResult)RdfSparql.ExecuteDataset(snapshot, query, default);
        Assert.AreEqual(new RdfIri("urn:c"), result.Bindings.Single()["o"]);
        Assert.IsTrue(snapshot.PrefixReads > 0);
        Assert.AreEqual(1, snapshot.Disposals);
    }

    private sealed class PrefixOnlySnapshot : RdfSparqlDatasetSnapshot
    {
        private readonly RdfQuad[] quads =
        [
            new(new RdfIri("urn:a"), new("urn:p"), new RdfIri("urn:b")),
            new(new RdfIri("urn:b"), new("urn:p"), new RdfIri("urn:c")),
            new(new RdfIri("urn:a"), new("urn:p"), new RdfIri("urn:b"), new RdfIri("urn:g")),
            new(new RdfIri("urn:b"), new("urn:p"), new RdfIri("urn:c"), new RdfIri("urn:g"))
        ];

        internal int PrefixReads;
        internal int Disposals;

        internal override DatabaseVersion Version => new(1);

        internal override IReadOnlyList<RdfQuad> Find(RdfQuadPattern pattern) =>
            throw new AssertFailedException("Path evaluation requested an unbounded relation.");

        internal override int EstimateCandidates(RdfQuadPattern pattern) => quads.Length;

        internal override IReadOnlyList<RdfQuad> ReadPrefix(RdfQuadPattern pattern, int maximumResults,
            bool namedOnly, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Assert.IsTrue(maximumResults is > 0 and <= 100_001);
            PrefixReads++;
            return quads.Where(quad => (pattern.Subject is null || quad.Subject == pattern.Subject) &&
                (pattern.Predicate is null || quad.Predicate == pattern.Predicate) &&
                (pattern.Object is null || quad.Object == pattern.Object) &&
                (pattern.Graph is null || quad.Graph == pattern.Graph) &&
                (!pattern.MatchDefaultGraphOnly || quad.Graph is null) && (!namedOnly || quad.Graph is not null))
                .Take(maximumResults).ToArray();
        }

        public override void Dispose() => Disposals++;
    }

    [TestMethod]
    public void Adapter_prefix_checks_cancellation_while_rejecting_candidates_and_zero_limit_reads_nothing()
    {
        using var cancellation = new CancellationTokenSource();
        var relation = new CancellingRelation(cancellation);
        using var snapshot = new RelationRdfSparqlDatasetSnapshot(relation);
        Assert.AreEqual(0, snapshot.ReadPrefix(new(), 0, false, default).Count);
        Assert.AreEqual(0, relation.Reads);
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            snapshot.ReadPrefix(new(Predicate: new("urn:missing")), 10, false, cancellation.Token));
        Assert.AreEqual(3, relation.Reads);
    }

    private sealed class CancellingRelation(CancellationTokenSource cancellation) : IReadOnlyList<RdfQuad>
    {
        internal int Reads;

        public int Count => 10;

        public RdfQuad this[int index] => new(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("value"));

        public IEnumerator<RdfQuad> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                Reads++;
                Assert.IsTrue(Reads <= 3, "Prefix read continued inspecting candidates after cancellation.");
                if (Reads == 3)
                {
                    cancellation.Cancel();
                }
                yield return this[i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [TestMethod]
    [DataRow("CONSTRUCT WHERE { GRAPH <urn:g> { ?s ?p ?o } }")]
    [DataRow("CONSTRUCT { GRAPH <urn:g> { ?s ?p ?o } } WHERE { ?s ?p ?o }")]
    [DataRow("CONSTRUCT { GRAPH <urn:g> {} } WHERE {}")]
    public void Query_templates_reject_named_graphs_even_when_no_rows_match(string query)
    {
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.ExecuteRelation([], query, default));
    }

    [TestMethod]
    public void Construct_can_read_named_graphs_but_returns_an_RDF_graph()
    {
        var snapshot = new TestSnapshot();
        var result = (RdfSparqlGraphResult)RdfSparql.ExecuteDataset(snapshot,
            "CONSTRUCT { ?s <urn:p> ?v } WHERE { GRAPH <urn:g> { ?s <urn:p> ?v } }", default);
        Assert.AreEqual(1, result.Quads.Count);
        Assert.IsNull(result.Quads[0].Graph);
        Assert.AreEqual("named", ((RdfLiteral)result.Quads[0].Object).LexicalForm);
        Assert.AreEqual(1, snapshot.DisposeCount);
    }

    [TestMethod]
    [DataRow("DESCRIBE <urn:a> FROM NAMED <urn:g>")]
    [DataRow("DESCRIBE ?s FROM NAMED <urn:g> WHERE { GRAPH <urn:g> { ?s ?p ?o } }")]
    public void Describe_adapter_returns_an_RDF_graph_and_releases_its_snapshot(string query)
    {
        var snapshot = new TestSnapshot();
        var result = (RdfSparqlGraphResult)RdfSparql.ExecuteDataset(snapshot, query, default);
        Assert.AreEqual(RdfSparqlQueryForm.Describe, result.Form);
        Assert.AreEqual(new DatabaseVersion(42), result.SnapshotVersion);
        Assert.AreEqual(1, result.Quads.Count);
        Assert.AreEqual(new RdfQuad(new RdfIri("urn:a"), new("urn:p"), new RdfLiteral("named")), result.Quads[0]);
        Assert.AreEqual(1, snapshot.DisposeCount);
    }

    [TestMethod]
    public void Delete_where_retains_quad_pattern_semantics_without_extending_query_templates()
    {
        RdfQuad[] input =
        [
            new(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("default")),
            new(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("named"), new RdfIri("urn:g"))
        ];
        var mutations = RdfSparqlUpdate.ParseWhere(input,
            "DELETE WHERE { GRAPH ?g { ?s ?p ?o } }", "query-template-regression", default);
        Assert.AreEqual(1, mutations.Count);
        Assert.AreEqual(input[1], ((RetractRdfQuad)mutations[0]).Quad);
    }

    [TestMethod]
    [DataRow("SELECT ?v WHERE { ?s <urn:p> ?v } ORDER BY ?v", "a,b")]
    [DataRow("SELECT ?v WHERE { GRAPH <urn:g> { ?s <urn:p> ?v } }", "named")]
    [DataRow("SELECT ?v WHERE { ?s <urn:p> ?v VALUES ?repeat { 1 1 } } ORDER BY ?v", "a,a,b,b")]
    [DataRow("SELECT (COUNT(?s) AS ?v) WHERE { ?s <urn:p> ?o }", "2")]
    [DataRow("SELECT ?v WHERE { ?s <urn:p> ?v FILTER(EXISTS { ?s <urn:p> 'a' }) }", "a")]
    public void Shared_evaluator_uses_adapter_dataset_and_preserves_bag_semantics(string query, string expected)
    {
        var snapshot = new TestSnapshot();
        var result = (RdfSparqlSelectResult)RdfSparql.ExecuteDataset(snapshot, query, default);
        Assert.AreEqual(expected, string.Join(',', result.Bindings.Select(row => ((RdfLiteral)row["v"]).LexicalForm)));
        Assert.AreEqual(new DatabaseVersion(42), result.SnapshotVersion);
        Assert.AreEqual(1, snapshot.DisposeCount);
        Assert.IsTrue(snapshot.Reads > 0);
    }

    [TestMethod]
    public void Adapter_snapshot_is_released_on_parse_failure()
    {
        var snapshot = new TestSnapshot();
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.ExecuteDataset(snapshot, "SELECT ?s WHERE {", default));
        Assert.AreEqual(1, snapshot.DisposeCount);
    }

    [TestMethod]
    public void Cancellation_releases_adapter_snapshot_without_reading()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var snapshot = new TestSnapshot();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            RdfSparql.ExecuteDataset(snapshot, "SELECT * WHERE { ?s ?p ?o }", cancellation.Token));
        Assert.AreEqual(1, snapshot.DisposeCount);
        Assert.AreEqual(0, snapshot.Reads);
    }

    [TestMethod]
    public void Cancellation_during_adapter_access_prevents_successful_result()
    {
        using var cancellation = new CancellationTokenSource();
        var snapshot = new TestSnapshot { OnRead = cancellation.Cancel };
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            RdfSparql.ExecuteDataset(snapshot, "SELECT * WHERE { ?s ?p ?o }", cancellation.Token));
        Assert.AreEqual(1, snapshot.DisposeCount);
        Assert.AreEqual(1, snapshot.Reads);
    }

    private sealed class TestSnapshot : RdfSparqlDatasetSnapshot
    {
        private readonly RdfQuad[] quads =
        [
            new(new RdfIri("urn:a"), new("urn:p"), new RdfLiteral("a")),
            new(new RdfIri("urn:b"), new("urn:p"), new RdfLiteral("b")),
            new(new RdfIri("urn:a"), new("urn:p"), new RdfLiteral("named"), new RdfIri("urn:g"))
        ];

        internal int Reads
        {
            get; private set;
        }

        internal int DisposeCount
        {
            get; private set;
        }

        internal Action? OnRead
        {
            get; init;
        }

        internal override DatabaseVersion Version => new(42);

        internal override IReadOnlyList<RdfQuad> ReadPrefix(RdfQuadPattern pattern, int maximumResults,
            bool namedOnly, CancellationToken cancellation) => Find(pattern)
                .Where(quad => !namedOnly || quad.Graph is not null).Take(maximumResults).ToArray();

        internal override IReadOnlyList<RdfQuad> Find(RdfQuadPattern pattern)
        {
            Assert.AreEqual(0, DisposeCount);
            Reads++;
            OnRead?.Invoke();
            return quads.Where(quad => (pattern.Subject is null || quad.Subject == pattern.Subject) &&
                (pattern.Predicate is null || quad.Predicate == pattern.Predicate) &&
                (pattern.Object is null || quad.Object == pattern.Object) &&
                (pattern.Graph is null || quad.Graph == pattern.Graph) &&
                (!pattern.MatchDefaultGraphOnly || quad.Graph is null)).ToArray();
        }

        public override void Dispose() => DisposeCount++;
    }
}
