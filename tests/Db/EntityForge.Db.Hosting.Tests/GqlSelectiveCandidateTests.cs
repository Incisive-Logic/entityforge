using EntityForge.Db.Gql;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlSelectiveCandidateTests
{
    private static GraphQuerySnapshot Snapshot(IEnumerable<GraphVertex> vertices, IEnumerable<GraphEdge>? edges = null)
    {
        var view = GraphReadView.Build(vertices.ToDictionary(v => v.Id), (edges ?? []).ToDictionary(e => e.Id));
        return new(new(1), view, view.StatisticsFor(new(1)));
    }

    private static GraphVertex Vertex(DatabaseValue? value) => new(GraphVertexId.New(), new HashSet<string> { "Item" },
        value is null ? new Dictionary<string, DatabaseValue>() : new Dictionary<string, DatabaseValue> { ["key"] = value });

    private static async Task<string[]> Rows(GraphQuerySnapshot snapshot, string source, bool scan)
    {
        var query = new GraphGqlQueryCompiler().CompileStandard(new(source)).Query;
        var result = await new BoundedGraphPatternExecutor(null).ExecuteCapturedResultAsync(query, snapshot, default,
            startCandidates: scan ? snapshot.View.Vertices.Keys.ToArray() : null);
        static string Key(QueryValue v) => v switch
        {
            NodeValue n => "node:" + n.Identity,
            EdgeValue e => "edge:" + e.Identity + ":" + e.Source + ":" + e.Target,
            PathValue p => String.Join("/", p.Elements.Select(Key)),
            _ => v.ToString()!
        };
        return ((GraphPatternResult)result.Result).Matches.Select(m =>
            JsonSerializer.Serialize(m.Bindings.OrderBy(p => p.Key).Select(p => p.Key + "=" + Key(p.Value))) +
            JsonSerializer.Serialize(m.Paths.Select(Key))).Order().ToArray();
    }

    [TestMethod]
    [DataRow("n.key>=0.1 AND n.key<1", 2)]
    [DataRow("n.key>0.1 AND n.key<1", 1)]
    [DataRow("1>=n.key AND n.key>=1", 3)]
    [DataRow("n.key>9007199254740992 AND n.key<9007199254740994", 1)]
    [DataRow("n.key<0", 1)]
    [DataRow("n.key<=0", 3)]
    [DataRow("n.key>= -1 AND n.key<=1", 7)]
    [DataRow("n.key>=1 AND n.key<1", 0)]
    [DataRow("n.key>=0+0 AND n.key<0.1+0", 2)]
    public async Task Scalar_range_candidates_preserve_exact_numeric_comparisons(string predicate, int expected)
    {
        var snapshot = Snapshot(new DatabaseValue?[]
        {
            new DatabaseValue.Int64(0), new DatabaseValue.Double(-0d), new DatabaseValue.Decimal(.1m), new DatabaseValue.Double(.1d),
            new DatabaseValue.Int64(1), new DatabaseValue.Decimal(1m), new DatabaseValue.Double(1d),
            new DatabaseValue.Int64(9007199254740993), new DatabaseValue.Double(9007199254740992),
            new DatabaseValue.Double(Double.PositiveInfinity), new DatabaseValue.Double(Double.NegativeInfinity),
            new DatabaseValue.Double(Double.NaN), null
        }.Select(Vertex));
        string source = "MATCH (n:Item) WHERE " + predicate + " RETURN n";
        var query = new GraphGqlQueryCompiler().CompileStandard(new(source)).Query;
        var description = new BoundedGraphPatternExecutor(null).DescribeCandidates(query, snapshot);
        Assert.AreEqual("snapshot.graph-scalar-numeric-range", description.AccessPaths.Single());
        var optimized = await Rows(snapshot, source, false);
        Assert.AreEqual(expected, optimized.Length);
        CollectionAssert.AreEqual(await Rows(snapshot, source, true), optimized);
    }

    [TestMethod]
    [DataRow("n.key<0 OR n.key IS NULL")]
    [DataRow("NOT(n.key>=0)")]
    public async Task Range_candidates_do_not_push_disjunction_or_negation(string predicate)
    {
        var snapshot = Snapshot(new[] { Vertex(new DatabaseValue.Int64(-1)), Vertex(new DatabaseValue.Int64(1)), Vertex(null) });
        string source = "MATCH (n:Item) WHERE " + predicate + " RETURN n";
        CollectionAssert.AreEqual(await Rows(snapshot, source, true), await Rows(snapshot, source, false));
        Assert.AreEqual("snapshot.graph-vertex-scan", new BoundedGraphPatternExecutor(null).DescribeCandidates(
            new GraphGqlQueryCompiler().CompileStandard(new(source)).Query, snapshot).AccessPaths.Single());
    }

    [TestMethod]
    public async Task Range_candidates_preserve_incompatible_type_errors_and_empty_input()
    {
        var snapshot = Snapshot(new[] { Vertex(new DatabaseValue.Int64(1)), Vertex(new DatabaseValue.String("2")) });
        const string query = "MATCH (n:Item) WHERE n.key>0 RETURN n";
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Rows(snapshot, query, true));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Rows(snapshot, query, false));
        const string zero = "MATCH (n:Item) WHERE n.key>1/0 RETURN n";
        CollectionAssert.AreEqual(await Rows(Snapshot([]), zero, true), await Rows(Snapshot([]), zero, false));
    }

    [TestMethod]
    [DataRow("MATCH (a)-[e:link]->(b:Item) WHERE b.key=7 RETURN a,e,b", true)]
    [DataRow("MATCH (a)<-[e:link]-(b:Item) WHERE b.key=7 RETURN a,e,b", true)]
    [DataRow("MATCH (a)-[e:link]->(b:Item) WHERE b.key>=7 AND b.key<8 RETURN a,e,b", true)]
    [DataRow("MATCH (a)-[e:link]-(b:Item) WHERE b.key=7 RETURN a,e,b", false)]
    [DataRow("MATCH (a:Item) OPTIONAL MATCH (a)-[e:link]->(b) WHERE b.key=7 RETURN a,e,b", false)]
    [DataRow("MATCH (a)-[e:link]->(b:Item) WHERE b.key=7 OR a.key=2 RETURN a,e,b", false)]
    public async Task Selective_target_preserves_bindings_paths_and_parallel_edge_bags(string source, bool anchored)
    {
        var a = Vertex(new DatabaseValue.Int64(2));
        var b = Vertex(new DatabaseValue.Int64(7));
        var c = Vertex(null);
        GraphEdge Edge(GraphVertex from, GraphVertex to) => new(GraphEdgeId.New(), from.Id, to.Id, "link", new Dictionary<string, DatabaseValue>());
        var snapshot = Snapshot([a, b, c], [Edge(a, b), Edge(a, b), Edge(b, a), Edge(b, b)]);
        var query = new GraphGqlQueryCompiler().CompileStandard(new(source)).Query;
        var path = new BoundedGraphPatternExecutor(null).DescribeCandidates(query, snapshot).AccessPaths.Single();
        Assert.AreEqual(anchored, path == "snapshot.graph-selective-target-adjacency");
        CollectionAssert.AreEqual(await Rows(snapshot, source, true), await Rows(snapshot, source, false));
        if (source.StartsWith("MATCH (a)-[e:link]->", StringComparison.Ordinal) && anchored)
        {
            Assert.AreEqual(3, (await Rows(snapshot, source, false)).Length, "Two parallel edges plus a self loop must remain distinct.");
        }
    }

    [TestMethod]
    public async Task Selective_target_falls_back_for_partial_adjacency_and_quantified_paths()
    {
        var a = Vertex(new DatabaseValue.Int64(2));
        var b = Vertex(new DatabaseValue.Int64(7));
        var edge = new GraphEdge(GraphEdgeId.New(), a.Id, b.Id, "link", new Dictionary<string, DatabaseValue>(), GraphEdgeStorageRole.OutgoingAdjacency);
        var snapshot = Snapshot([a, b], [edge]);
        const string query = "MATCH (a)-[e:link]->(b) WHERE b.key=7 RETURN a,e,b";
        Assert.AreEqual(1, (await Rows(snapshot, query, false)).Length);
        Assert.AreEqual("snapshot.graph-vertex-scan", new BoundedGraphPatternExecutor(null).DescribeCandidates(
            new GraphGqlQueryCompiler().CompileStandard(new(query)).Query, snapshot).AccessPaths.Single());
        const string quantified = "MATCH (a)-[:link]->{0,2}(b) WHERE b.key=7 RETURN a,b";
        CollectionAssert.AreEqual(await Rows(snapshot, quantified, true), await Rows(snapshot, quantified, false));
    }

    [TestMethod]
    public async Task Range_index_concurrent_queries_and_cancellation_preserve_results()
    {
        var snapshot = Snapshot(Enumerable.Range(0, 1000).Select(i => Vertex(new DatabaseValue.Int64(i))));
        const string source = "MATCH (n) WHERE n.key>=500 AND n.key<510 RETURN n";
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => Rows(snapshot, source, false))));
        foreach (var rows in results)
        {
            Assert.AreEqual(10, rows.Length);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => snapshot.View.FindScalarNumericRange(
            [new("key", new LiteralValue("500", QueryScalarKind.Integer), ">=")], cancellation.Token));
    }
}

public sealed partial class GraphDatabaseEngineTests
{
    [TestMethod]
    public async Task Scalar_range_index_tracks_changed_values_and_retained_snapshots()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var vertices = Enumerable.Range(0, 10_100).Select(i => new GraphVertex(GraphVertexId.New(), new HashSet<string> { "Item" },
            new Dictionary<string, DatabaseValue> { ["key"] = new DatabaseValue.Int64(i) })).ToArray();
        var version = await database.BulkLoadAsync(Guid.NewGuid(), vertices, []);
        using var snapshot = await database.AcquireSnapshotAsync(version);
        var query = new GraphGqlQueryCompiler().CompileStandard(new("MATCH (n:Item) WHERE n.key>=500 AND n.key<510 RETURN n")).Query;
        async Task<int> Count(DatabaseVersion? at = null) => ((GraphPatternResult)(await database.QueryAsync(query, at)).Result).Matches.Count;
        Assert.AreEqual(10, await Count());
        await database.CommitAsync(new(Guid.NewGuid(), [new SetVertexProperty(vertices[500].Id, "other", new DatabaseValue.Int64(1))]));
        Assert.AreEqual(10, await Count());
        await database.CommitAsync(new(Guid.NewGuid(), [new SetVertexProperty(vertices[500].Id, "key", new DatabaseValue.Int64(-1))]));
        Assert.AreEqual(9, await Count());
        Assert.AreEqual(10, await Count(version));
        var id = GraphVertexId.New();
        await database.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(id, ["Item"]), new SetVertexProperty(id, "key", new DatabaseValue.Int64(505))]));
        Assert.AreEqual(10, await Count());
        await database.CommitAsync(new(Guid.NewGuid(), [new DeleteVertex(id)]));
        Assert.AreEqual(9, await Count());
    }
}
