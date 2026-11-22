using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Hosting;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GraphFragmentTests
{
    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    [DataTestMethod]
    [DataRow("MATCH (n:Person) WHERE n.key = \"0\" RETURN n")]
    [DataRow("MATCH (n:Person) RETURN n LIMIT 2 OFFSET 1")]
    [DataRow("MATCH (n:Person) WHERE n.key = \"missing\" RETURN n")]
    [DataRow("MATCH (n:Person)-[e:peer]->(t:Person) WHERE n.key = \"0\" RETURN t")]
    [DataRow("MATCH (n:Person)-[e:peer]->(t:Person) WHERE n.key = \"0\" RETURN n, t LIMIT 2 OFFSET 1")]
    [DataRow("MATCH (n:Person)-[e:peer]->(m:Person)-[f:peer]->(t:Person) WHERE n.key = \"0\" RETURN t")]
    [DataRow("MATCH (n:Person)<-[e:peer]-(t:Person) WHERE n.key = \"2\" RETURN n, t")]
    [DataRow("MATCH (n:Person)-[e:peer]->(t:Person) WHERE n.key = \"0\" AND t.key = \"2\" RETURN t.key AS value")]
    [DataRow("MATCH (n:Person)-[e:peer]->(t:Person) WHERE n.key = \"0\" RETURN n.unprojected + t.unprojected AS total, t.missing AS missing")]
    [DataRow("MATCH (n:Person)-[e:peer]->(m:Person)-[f:peer]->(t:Person) WHERE n.key = \"0\" RETURN n.key || t.key AS text LIMIT 2 OFFSET 1")]
    [DataRow("MATCH (n:Person)-[e:peer]->(m:Person)-[f:peer]->(t:Person)-[g:peer]->(u:Person) WHERE n.key = \"0\" RETURN n, m, t, u LIMIT 4 OFFSET 1")]
    [DataRow("MATCH (n:Person)-[e:absent]->(t:Person) WHERE n.key = \"0\" RETURN t")]
    public async Task Fragments_preserve_rows_materialisation_and_evidence(string statement)
    {
        var vertices = Enumerable.Range(0, 4).Select(i => new GraphVertex(new(Id(i + 1)),
            new HashSet<string> { "Person" }, new Dictionary<string, DatabaseValue>
            {
                ["key"] = new DatabaseValue.String(i.ToString()),
                ["unprojected"] = new DatabaseValue.Int64(i)
            })).ToArray();
        GraphEdge E(int i, int from, int to) => new(new(Id(i)), vertices[from].Id, vertices[to].Id, "peer", new Dictionary<string, DatabaseValue>());
        // Parallel edges and a cycle must preserve row occurrences, including
        // multiple paths to the same target. Native lookup deduplication is safe;
        // deduplicating these rows is not.
        GraphEdge[] edges = [E(10, 0, 1), E(11, 0, 1), E(12, 0, 2), E(13, 1, 2), E(14, 2, 0)];
        var view = GraphReadView.Build(vertices.ToDictionary(v => v.Id), edges.ToDictionary(e => e.Id));
        var compiled = new GraphGqlQueryCompiler().Compile(new(statement));
        var query = compiled.Query;
        Assert.IsTrue(BoundedGraphPatternExecutor.SupportsFragments(query));
        var expected = await new BoundedGraphPatternExecutor(null, capturedView: view)
            .ExecuteCapturedResultAsync(query, new(default, view, view.StatisticsFor(default)), default);
        var roots = BoundedGraphPatternExecutor.FragmentVertices(view, query, 0, null).ToArray();
        var bindings = new BoundedGraphPatternExecutor.FragmentBindings(query, roots);
        var frontier = roots;
        for (int stage = 1; stage <= query.Relationships.Count; stage++)
        {
            var relationship = query.Relationships[stage - 1];
            var ids = frontier.Select(v => v.Id).ToHashSet();
            var selectedEdges = edges.Where(e => (relationship.Label is null || relationship.Label == e.Label) &&
                ids.Contains(relationship.Direction == EntityForge.Query.Querying.GraphPatternDirection.Outgoing ? e.From : e.To)).ToArray();
            var targets = selectedEdges.Select(e => relationship.Direction == EntityForge.Query.Querying.GraphPatternDirection.Outgoing ? e.To.Value : e.From.Value).Distinct().ToArray();
            frontier = BoundedGraphPatternExecutor.FragmentVertices(view, query, stage, targets).ToArray();
            bindings.Advance(selectedEdges, frontier.ToDictionary(v => v.Id), default);
        }
        var actual = bindings.Complete(default, compiled.Projection is { } p
            ? property => GraphGqlPropertyProjectionShaper.Project(p, property) : null, preserveGraph: true);
        if (compiled.Projection is { } projection)
        {
            expected = GraphGqlPropertyProjectionShaper.Apply(expected, projection);
        }
        var expectedResponse = GraphPatternQueryResponseMapper.Map(expected, 0);
        var actualResponse = GraphPatternQueryResponseMapper.Map(actual, 0) with
        {
            QueryId = expectedResponse.QueryId
        };
        Assert.AreEqual(JsonSerializer.Serialize(expectedResponse), JsonSerializer.Serialize(actualResponse), statement);
    }

    [DataTestMethod]
    [DataRow("MATCH (n)-[e]->(t) RETURN e")]
    [DataRow("MATCH (n) OPTIONAL MATCH (n)-[e]->(t) WHERE n.key = \"0\" RETURN t")]
    [DataRow("MATCH (n) RETURN COUNT(n) AS count")]
    [DataRow("MATCH (n) RETURN DISTINCT n")]
    [DataRow("MATCH (n) WHERE NOT (n.key != \"0\") RETURN n")]
    [DataRow("MATCH (n) RETURN n ORDER BY n.key")]
    public void Unsupported_semantics_select_fallback_before_execution(string statement)
    {
        var compiled = new GraphGqlQueryCompiler().Compile(new(statement));
        Assert.IsTrue(compiled.RequiresCompleteGraph || !BoundedGraphPatternExecutor.SupportsFragments(compiled.Query));
    }

    [TestMethod]
    public void Incomplete_dependencies_cannot_produce_success()
    {
        var query = new GraphGqlQueryCompiler().Compile(new("MATCH (n)-[e]->(t) WHERE n.key = \"0\" RETURN t")).Query;
        var vertex = new GraphVertex(new(Id(1)), new HashSet<string>(), new Dictionary<string, DatabaseValue>());
        var bindings = new BoundedGraphPatternExecutor.FragmentBindings(query, [vertex]);
        Assert.ThrowsExactly<InvalidOperationException>(() => bindings.Complete(default));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => bindings.Advance([], new Dictionary<GraphVertexId, GraphVertex>(), stop.Token));
    }

    [TestMethod]
    public void Binding_multiplication_obeys_expansion_bound_before_adding_rows()
    {
        var original = new GraphGqlQueryCompiler().Compile(new("MATCH (n)-[e]->(t) WHERE n.key = \"0\" RETURN t")).Query;
        var query = new EntityForge.Query.Querying.BoundedGraphPatternQuery(original.Start,
            original.Relationships, maximumExpansions: 3, returnedBindings: original.ReturnedBindings, filter: original.Filter);
        var root = new GraphVertex(new(Id(1)), new HashSet<string>(), new Dictionary<string, DatabaseValue>());
        var target = new GraphVertex(new(Id(2)), new HashSet<string>(), new Dictionary<string, DatabaseValue>());
        var edges = Enumerable.Range(10, 4).Select(i => new GraphEdge(new(Id(i)), root.Id, target.Id, "peer",
            new Dictionary<string, DatabaseValue>())).ToArray();
        var bindings = new BoundedGraphPatternExecutor.FragmentBindings(query, [root]);
        Assert.ThrowsExactly<EntityForge.Query.Querying.GraphPatternExpansionLimitException>(() =>
            bindings.Advance(edges, new Dictionary<GraphVertexId, GraphVertex> { [target.Id] = target }, default));
        Assert.ThrowsExactly<InvalidOperationException>(() => bindings.Complete(default));
    }
}
