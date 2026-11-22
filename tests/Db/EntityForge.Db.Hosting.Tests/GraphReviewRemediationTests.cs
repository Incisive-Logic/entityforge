using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Storage.Pages;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;
using EntityForge.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GraphReviewRemediationTests
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ef-graph-review-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_path))
        {
            Directory.Delete(_path, true);
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public async Task Unbounded_shortest_walk_retains_expansion_budget_and_recovers_after_failure(int count)
    {
        await using var db = await GraphDatabase.OpenLocalAsync(new(_path));
        var node = GraphVertexId.New();
        await db.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(node, ["Start"]),
            new CreateEdge(GraphEdgeId.New(), node, node, "loop"),
            new CreateEdge(GraphEdgeId.New(), node, node, "loop")]));
        BoundedGraphPatternQuery Query(int minimum, int budget, int selected) => new(new("n", ["Start"]),
            [new("r", "n", "loop", GraphPatternDirection.Outgoing, new("m"), hops: new(minimum, null),
                pathSelection: GraphPatternPathSelection.AllShortest, allowRepeatedRelationships: true, pathSelectionCount: selected)],
            maximumMatches: 1, maximumExpansions: budget);
        await Assert.ThrowsAsync<GraphPatternExpansionLimitException>(async () => await db.QueryAsync(Query(20, 20, count)));
        if (count > 1)
        {
            await Assert.ThrowsAsync<GraphPatternExpansionLimitException>(async () => await db.QueryAsync(Query(0, 4, count)));
        }
        var recovered = await db.QueryAsync(Query(0, 20, 1));
        Assert.AreEqual(1, ((GraphPatternResult)recovered.Result).Matches.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Zero_count_shortest_walk_avoids_expansion_and_preserves_optional_rows(bool optional)
    {
        await using var db = await GraphDatabase.OpenLocalAsync(new(_path));
        var node = GraphVertexId.New();
        await db.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(node, ["Start"]),
            new CreateEdge(GraphEdgeId.New(), node, node, "loop"),
            new CreateEdge(GraphEdgeId.New(), node, node, "loop")]));
        var query = new BoundedGraphPatternQuery(new("n", ["Start"]),
            [new("r", "n", "loop", GraphPatternDirection.Outgoing, new("m"),
                matchKind: optional ? GraphPatternMatchKind.Optional : GraphPatternMatchKind.Required,
                hops: new(20, null), pathSelection: GraphPatternPathSelection.AllShortest,
                allowRepeatedRelationships: true, pathSelectionCount: 0)],
            returnedBindings: ["n", "m"], maximumExpansions: 1);
        var result = (GraphPatternResult)(await db.QueryAsync(query)).Result;
        Assert.AreEqual(optional ? 1 : 0, result.Matches.Count);
        if (optional)
        {
            Assert.IsInstanceOfType<UnboundValue>(result.Matches.Single().Bindings["m"]);
        }
        var analysis = (await db.ExplainAsync(query, analyze: true)).RelationshipAnalysis!.Single();
        Assert.AreEqual(0L, analysis.InspectedEdges);
        Assert.AreEqual(0L, analysis.EnumeratedPaths);
        Assert.AreEqual("shortest-0-groups", analysis.PathSelection);
    }

    [TestMethod]
    public async Task Open_trail_materialization_is_bounded_even_when_row_limit_is_small()
    {
        await using var db = await GraphDatabase.OpenLocalAsync(new(_path));
        GraphVertexId[] nodes = Enumerable.Range(0, 1001).Select(_ => GraphVertexId.New()).ToArray();
        var mutations = nodes.Select((node, index) => (GraphMutation)new CreateVertex(node, index == 0 ? ["Start"] : ["L"])).ToList();
        mutations.AddRange(Enumerable.Range(0, 1000).Select(index =>
            (GraphMutation)new CreateEdge(GraphEdgeId.New(), nodes[index], nodes[index + 1], "next")));
        await db.CommitAsync(new(Guid.NewGuid(), mutations));
        var query = new BoundedGraphPatternQuery(new("n", ["Start"]),
            [new("r", "n", "next", GraphPatternDirection.Outgoing, new("m"), hops: new(1, null))],
            maximumMatches: 1, maximumExpansions: 3000);
        var error = await Assert.ThrowsAsync<ArgumentException>(async () => await db.QueryAsync(query));
        StringAssert.Contains(error.Message, "materialized-element");
    }

    [TestMethod]
    public async Task Self_converging_count_requires_a_self_loop()
    {
        await using var db = await GraphDatabase.OpenLocalAsync(new(_path));
        var a = GraphVertexId.New();
        var b = GraphVertexId.New();
        await db.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(a, ["L"]),
            new CreateVertex(b, ["L"]),
            new CreateEdge(GraphEdgeId.New(), a, b, "r")]));
        var query = new BoundedGraphPatternQuery(new("n", ["L"]),
            [new("e", "n", "r", GraphPatternDirection.Outgoing, new("n", ["L"]))],
            aggregation: new([new(GraphPatternAggregateFunction.Count, "count")]));
        Assert.AreEqual("0", AggregateValue(await db.QueryAsync(query)));
        await db.CommitAsync(new(Guid.NewGuid(), [new CreateEdge(GraphEdgeId.New(), a, a, "r")]));
        Assert.AreEqual("1", AggregateValue(await db.QueryAsync(query)));
    }

    [TestMethod]
    public async Task Two_relationships_converging_on_one_target_decline_branch_optimizations()
    {
        await using var db = await GraphDatabase.OpenLocalAsync(new(_path));
        var a = GraphVertexId.New();
        var b = GraphVertexId.New();
        await db.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(a, ["A"]),
            new CreateVertex(b, ["B"]),
            new SetVertexProperty(b, "rank", new DatabaseValue.Int64(1)),
            new CreateEdge(GraphEdgeId.New(), a, b, "p"),
            new CreateEdge(GraphEdgeId.New(), a, b, "q")]));
        GraphPatternRelationship[] relationships = [
            new("p", "a", "p", GraphPatternDirection.Outgoing, new("b", ["B"])),
            new("q", "a", "q", GraphPatternDirection.Outgoing, new("b", ["B"]))];
        var query = new BoundedGraphPatternQuery(new("a", ["A"]), relationships, returnedBindings: ["b"],
            aggregation: new([new(GraphPatternAggregateFunction.Count, "count", "a")], [new("b", "b")]));
        Assert.AreEqual("1", AggregateValue(await db.QueryAsync(query)));
        var ordered = new BoundedGraphPatternQuery(new("a", ["A"]), relationships,
            returnedBindings: ["b"], orderBy: [new("b", "rank")]);
        Assert.AreEqual(1, ((GraphPatternResult)(await db.QueryAsync(ordered)).Result).Matches.Count);
        // Unique targets can still be ineligible: neither target is the returned root.
        var rootOrdered = new BoundedGraphPatternQuery(new("a", ["A"]),
            [relationships[0], new("q", "a", "q", GraphPatternDirection.Outgoing, new("c", ["B"]))],
            returnedBindings: ["a"], orderBy: [new("a", "rank")]);
        Assert.AreEqual(1, ((GraphPatternResult)(await db.QueryAsync(rootOrdered)).Result).Matches.Count);
    }

    [DataTestMethod]
    [DataRow(GraphPatternAggregateFunction.Count)]
    [DataRow(GraphPatternAggregateFunction.Sum)]
    [DataRow(GraphPatternAggregateFunction.Minimum)]
    [DataRow(GraphPatternAggregateFunction.Maximum)]
    [DataRow(GraphPatternAggregateFunction.Average)]
    public async Task Empty_aggregate_agrees_between_optimized_and_filtered_paths(GraphPatternAggregateFunction function)
    {
        await using var db = await GraphDatabase.OpenLocalAsync(new(_path));
        var aggregate = new GraphPatternAggregation([new(function, "value", "n", "p")]);
        var simple = new BoundedGraphPatternQuery(new("n", ["Missing"]), [], aggregation: aggregate);
        var filtered = new BoundedGraphPatternQuery(new("n", ["Missing"]), [], aggregation: aggregate,
            filter: new GraphPatternPropertyComparison("n", "p", new LiteralValue("1", QueryScalarKind.Integer)));
        string? expected = function is GraphPatternAggregateFunction.Count ? "0" : null;
        Assert.AreEqual(expected, AggregateValue(await db.QueryAsync(simple)));
        Assert.AreEqual(expected, AggregateValue(await db.QueryAsync(filtered)));
    }

    private sealed class Person
    {
        public Guid Id
        {
            get; set;
        }

        public string Name { get; set; } = "";
    }

    [TestMethod]
    public async Task Mapped_read_entrances_reject_foreign_and_disposed_leases()
    {
        await using var a = await GraphDatabase.OpenLocalAsync(new(Path.Combine(_path, "a")));
        await using var b = await GraphDatabase.OpenLocalAsync(new(Path.Combine(_path, "b")));
        var map = GraphEntityMap.For<Person>().VertexLabel("Person").Identity(p => p.Id, "id")
            .Property(p => p.Name, "name").Materialize(c => new Person { Id = c.IdentityAs<Guid>(), Name = c.Required<string>("Name") });
        Guid id = Guid.NewGuid();
        await a.EntityStore(map).SaveAsync(new Person { Id = id, Name = "A" });
        await b.EntityStore(map).SaveAsync(new Person { Id = id, Name = "B" });
        using var lease = a.AcquireSnapshot();
        using var own = b.AcquireSnapshot();
        Assert.AreEqual(lease.Version, own.Version);
        Assert.ThrowsException<ArgumentException>(() => b.GetEntity(map, id, lease));
        Assert.ThrowsException<ArgumentException>(() => b.ProjectEntity(map, id, lease));
        Assert.AreEqual("B", b.GetEntity(map, id, own)!.Name);
        Assert.IsNotNull(b.ProjectEntity(map, id, own));
        own.Dispose();
        Assert.ThrowsException<ObjectDisposedException>(() => b.GetEntity(map, id, own));
        Assert.ThrowsException<ObjectDisposedException>(() => b.ProjectEntity(map, id, own));
    }

    [TestMethod]
    public async Task First_bulk_commit_recovers_all_physical_pages_after_durable_unpublished_commit()
    {
        var from = GraphVertexId.New();
        var to = GraphVertexId.New();
        var edge = GraphEdgeId.New();
        await using (var db = await GraphDatabase.OpenLocalAsync(new(_path), new StopAfterWal(), null))
        {
            await Assert.ThrowsExceptionAsync<EntityForgeDbCommitException>(async () =>
                await db.BulkLoadAsync(Guid.NewGuid(),
                    [new(from, new HashSet<string>(), new Dictionary<string, DatabaseValue>()),
                        new(to, new HashSet<string>(), new Dictionary<string, DatabaseValue>())],
                    [new(edge, from, to, "r", new Dictionary<string, DatabaseValue>())]));
        }

        await using var recovered = await GraphDatabase.OpenLocalAsync(new(_path));
        Assert.IsNotNull(recovered.GetEdge(edge));
        Assert.AreEqual(edge, recovered.Outgoing(from).Single().Id);
        Assert.AreEqual(edge, recovered.Incoming(to).Single().Id);
    }

    private sealed class StopAfterWal : IRedoDurabilityObserver
    {
        public void Reached(RedoDurabilityStage stage)
        {
            if (stage == RedoDurabilityStage.AfterWalFlush)
            {
                throw new IOException("Stop before publication.");
            }
        }
    }

    private static string? AggregateValue(QueryResultEnvelope result)
    {
        var row = ((RecordResult)result.Result).Rows.Single();
        QueryValue value = row.Values.Last().Value;
        return value is LiteralValue literal ? literal.LexicalValue : null;
    }

    [TestMethod]
    public void Projected_collection_limit_uses_the_selected_predecessor_and_allows_reduction()
    {
        var planner = new GraphTransactionPlanner(JsonValueAdmissionPolicy.Default, maximumEntryCount: 2);
        var id = GraphVertexId.New();
        var edge = GraphEdgeId.New();
        var vertices = new Dictionary<GraphVertexId, GraphVertex> { [id] = new(id, new HashSet<string>(), new Dictionary<string, DatabaseValue>()) };
        var edges = new Dictionary<GraphEdgeId, GraphEdge> { [edge] = new(edge, id, id, "r", new Dictionary<string, DatabaseValue>()) };
        var first = planner.Plan(new(Guid.NewGuid(), [new SetVertexProperty(id, "a", new DatabaseValue.Boolean(true)),
            new SetEdgeProperty(edge, "a", new DatabaseValue.Boolean(true))]), vertices, edges);
        var second = planner.Plan(new(Guid.NewGuid(), [new SetVertexProperty(id, "b", new DatabaseValue.Boolean(true)),
            new SetEdgeProperty(edge, "b", new DatabaseValue.Boolean(true))]), first.Vertices, first.Edges);
        Assert.ThrowsException<InvalidOperationException>(() => planner.Plan(new(Guid.NewGuid(),
            [new SetVertexProperty(id, "c", new DatabaseValue.Boolean(true))]), second.Vertices, second.Edges));
        Assert.ThrowsException<InvalidOperationException>(() => planner.Plan(new(Guid.NewGuid(),
            [new SetEdgeProperty(edge, "c", new DatabaseValue.Boolean(true))]), second.Vertices, second.Edges));
        var reduced = planner.Plan(new(Guid.NewGuid(), [new RemoveVertexProperty(id, "a"), new RemoveEdgeProperty(edge, "a")]), second.Vertices, second.Edges);
        var replacement = planner.Plan(new(Guid.NewGuid(), [new SetVertexProperty(id, "b", new DatabaseValue.Boolean(false))]), second.Vertices, second.Edges);
        Assert.AreEqual(1, reduced.Vertices[id].Properties.Count);
        Assert.AreEqual(2, replacement.Vertices[id].Properties.Count);
        Assert.AreEqual(2, second.Vertices[id].Properties.Count);
        var swapped = planner.Plan(new(Guid.NewGuid(), [new SetVertexProperty(id, "c", new DatabaseValue.Boolean(true)),
            new RemoveVertexProperty(id, "a")]), second.Vertices, second.Edges);
        Assert.AreEqual(2, swapped.Vertices[id].Properties.Count);
        Assert.ThrowsException<InvalidOperationException>(() => planner.Plan(new(Guid.NewGuid(),
            [new CreateVertex(GraphVertexId.New(), ["one", "two", "three"])]), vertices, edges));
        Assert.ThrowsException<InvalidOperationException>(() => planner.Plan(new(Guid.NewGuid(),
            [new CreateVertex(GraphVertexId.New(), []), new CreateVertex(GraphVertexId.New(), [])]), vertices, edges));
        Assert.ThrowsException<InvalidOperationException>(() => planner.Plan(new(Guid.NewGuid(),
            [new CreateEdge(GraphEdgeId.New(), id, id, "r"), new CreateEdge(GraphEdgeId.New(), id, id, "r")]), vertices, edges));
    }
}
