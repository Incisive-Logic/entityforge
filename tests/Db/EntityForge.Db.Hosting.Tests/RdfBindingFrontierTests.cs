using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using System.Text.Json;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    private const string FrontierQuery = "SELECT ?number ?name WHERE { ?root a <urn:Item> . ?root <urn:number> ?number . ?root <urn:name> ?name . ?s <urn:number> 7 . ?s <urn:peer> ?m . ?m <urn:peer> ?root . }";

    [DataTestMethod]
    [DataRow(RdfSparql.MaximumResults + 1)]
    [DataRow(100_000)]
    public async Task Rdf_frontier_avoids_whole_relation_capture_above_the_unchanged_row_limit(int count)
    {
        await using var database = await OpenAsync(CreateDirectory());
        RdfIri Subject(int i) => new("urn:item:" + i);
        var quads = Enumerable.Range(0, count).SelectMany(i => new RdfQuad[]
        {
            new(Subject(i), new("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"), new RdfIri("urn:Item")),
            new(Subject(i), new("urn:number"), new RdfLiteral(i.ToString(), "http://www.w3.org/2001/XMLSchema#integer")),
            new(Subject(i), new("urn:name"), new RdfLiteral("name-" + i)),
            new(Subject(i), new("urn:peer"), Subject((i + 1) % count))
        }).ToArray();
        var version = await database.CommitAsync(new(Guid.NewGuid(), quads.Select(q => (RdfMutation)new AssertRdfQuad(q)).ToArray()));
        using var snapshot = await database.AcquireSnapshotAsync(version);
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.CaptureBindingFragments(database, snapshot, FrontierQuery, count, default));
        int exchanged = 0, stages = 0;
        async Task<RdfSparqlSelectResult> Run(CancellationToken token = default) => (RdfSparqlSelectResult)await RdfSparql.ExecuteBindingFrontierAsync(FrontierQuery, (stage, ct) =>
        {
            var rows = RdfSparql.CaptureBindingFrontier(database, snapshot, FrontierQuery, stage, count, ct);
            exchanged += rows.Length;
            stages++;
            return ValueTask.FromResult<IReadOnlyList<RdfBindingFragment>>(rows);
        }, token);
        var actual = await Run();
        Assert.AreEqual(6, stages);
        Assert.AreEqual(6, exchanged);
        Assert.HasCount(1, actual.Bindings);
        Assert.AreEqual(new RdfLiteral("9", "http://www.w3.org/2001/XMLSchema#integer"), actual.Bindings[0]["number"]);
        Assert.AreEqual(new RdfLiteral("name-9"), actual.Bindings[0]["name"]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Run(cancelled.Token));
        await database.CommitAsync(new(Guid.NewGuid(), [new RetractRdfQuad(new(Subject(9), new("urn:name"), new RdfLiteral("name-9"))),
            new AssertRdfQuad(new(Subject(9), new("urn:name"), new RdfLiteral("changed")))]));
        Assert.AreEqual(new RdfLiteral("name-9"), (await Run()).Bindings[0]["name"], "Every stage retains the captured snapshot.");
    }

    [TestMethod]
    public async Task Rdf_frontier_preserves_bags_named_graphs_repeated_variables_and_global_modifiers()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var quads = new RdfQuad[]
        {
            new(new RdfIri("urn:start"), new("urn:p"), new RdfIri("urn:a")),
            new(new RdfIri("urn:start"), new("urn:p"), new RdfIri("urn:b")),
            new(new RdfIri("urn:a"), new("urn:q"), new RdfLiteral("same")),
            new(new RdfIri("urn:b"), new("urn:q"), new RdfLiteral("same")),
            new(new RdfIri("urn:a"), new("urn:self"), new RdfIri("urn:a")),
            new(new RdfIri("urn:start"), new("urn:p"), new RdfIri("urn:a"), new RdfIri("urn:g")),
            new(new RdfIri("urn:a"), new("urn:q"), new RdfLiteral("named"), new RdfIri("urn:g"))
        };
        var version = await database.CommitAsync(new(Guid.NewGuid(), quads.Select(q => (RdfMutation)new AssertRdfQuad(q)).ToArray()));
        using var snapshot = await database.AcquireSnapshotAsync(version);
        string[] queries = [
            "SELECT ?v WHERE { <urn:start> <urn:p> ?x . ?x <urn:q> ?v } ORDER BY ?v",
            "SELECT DISTINCT ?v WHERE { <urn:start> <urn:p> ?x . ?x <urn:q> ?v }",
            "SELECT (COUNT(*) AS ?c) WHERE { <urn:start> <urn:p> ?x . ?x <urn:q> ?v }",
            "SELECT ?x WHERE { <urn:start> <urn:p> ?x . ?x <urn:q> ?v } ORDER BY ?x LIMIT 1 OFFSET 1",
            "SELECT ?x WHERE { <urn:start> <urn:p> ?x . ?x <urn:self> ?x }",
            "SELECT ?v WHERE { GRAPH ?g { <urn:start> <urn:p> ?x . ?x <urn:q> ?v } }",
            "SELECT ?v WHERE { <urn:missing> <urn:p> ?x . ?x <urn:q> ?v }"];
        foreach (string query in queries)
        {
            var expected = (RdfSparqlSelectResult)await database.QuerySparqlAsync(new EntityForge.Db.Rdf.Contracts.RdfSparqlQueryRequest(query, snapshotVersion: version));
            if (query.Contains("GRAPH", StringComparison.Ordinal))
            {
                Assert.IsFalse(RdfSparql.SupportsBindingFrontier(query));
                Assert.AreEqual(1, expected.Bindings.Count);
                continue;
            }
            var actual = (RdfSparqlSelectResult)await RdfSparql.ExecuteBindingFrontierAsync(query,
                (stage, ct) => ValueTask.FromResult<IReadOnlyList<RdfBindingFragment>>(RdfSparql.CaptureBindingFrontier(database, snapshot, query, stage, RdfSparql.MaximumResults, ct)), default);
            string Canonical(RdfSparqlSelectResult result) => JsonSerializer.Serialize(result.Bindings.Select(r => r.OrderBy(p => p.Key).ToArray()));
            Assert.AreEqual(Canonical(expected), Canonical(actual), query);
        }
        Assert.IsFalse(RdfSparql.SupportsBindingFrontier("SELECT ?x WHERE { <urn:start> <urn:p> ?x OPTIONAL { ?x <urn:q> ?v } }"));
        Assert.IsFalse(RdfSparql.SupportsBindingFrontier("SELECT ?x WHERE { <urn:start> <urn:p> ?x . ?x <urn:q> ?v FILTER(?v = \"same\") }"));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await RdfSparql.ExecuteBindingFrontierAsync(queries[0],
            (stage, ct) => ValueTask.FromResult<IReadOnlyList<RdfBindingFragment>>([new(stage.Pattern + 1, new())]), default));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.CaptureBindingFrontier(database, snapshot, queries[0], new(-1, []), RdfSparql.MaximumResults, default));
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.CaptureBindingFrontier(database, snapshot, queries[0], new(0, [new()]), 1, default));
    }
}
