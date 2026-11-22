using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT ?o { GRAPH <urn:missing> { <urn:a> <urn:p>* ?o } }", 0)]
    [DataRow("SELECT ?g ?o { GRAPH ?g { ?g <urn:p>* ?o } }", 0)]
    [DataRow("SELECT ?g ?o { GRAPH ?g { <urn:a> <urn:p>+ ?o } }", 1)]
    [DataRow("SELECT ?g ?s ?o { GRAPH ?g { ?s <urn:p>* ?o } }", 3)]
    [DataRow("SELECT ?o { <urn:a> !<urn:p> ?o }", 1)]
    [DataRow("SELECT ?o { <urn:a> !(<urn:p>|<urn:q>) ?o }", 0)]
    [DataRow("SELECT ?o { <urn:a> !() ?o }", 2)]
    [DataRow("SELECT ?o { <urn:a> !(^<urn:q>) ?o }", 1)]
    [DataRow("SELECT ?o { <urn:a> !(<urn:q>|^<urn:q>) ?o }", 3)]
    [DataRow("SELECT ?o { <urn:a> ^(!<urn:q>) ?o }", 1)]
    [DataRow("SELECT ?o { <urn:a> (!<urn:q>)+ ?o }", 4)]
    [DataRow("SELECT ?o { 'absent' <urn:p>* ?o }", 1)]
    [DataRow("SELECT ?o { <urn:a> <urn:p>+ ?o }", 4)]
    [DataRow("SELECT ?o { <urn:a> <urn:p>* ?o }", 4)]
    [DataRow("SELECT ?s { ?s <urn:p>+ <urn:a> }", 4)]
    [DataRow("SELECT ?s ?o { ?s <urn:p>+ ?o }", 16)]
    [DataRow("SELECT ?s { ?s <urn:p>+ ?s }", 4)]
    [DataRow("SELECT ?o { <urn:absent> <urn:p>* ?o }", 1)]
    [DataRow("SELECT ?o { <urn:absent> <urn:p>+ ?o }", 0)]
    [DataRow("SELECT ?o { <urn:a> <urn:p>/<urn:p> ?o }", 2)]
    [DataRow("SELECT ?o { <urn:a> (<urn:p>|<urn:q>) ?o }", 3)]
    [DataRow("SELECT ?o { <urn:a> (<urn:p>|<urn:q>)? ?o }", 3)]
    [DataRow("SELECT ?o { <urn:a> (<urn:p>/<urn:p>)+ ?o }", 4)]
    [DataRow("SELECT ?o { GRAPH <urn:g> { <urn:a> <urn:p>+ ?o } }", 1)]
    [DataRow("SELECT ?o { VALUES ?s { <urn:a> <urn:a> } ?s <urn:p>+ ?o }", 8)]
    [DataRow("SELECT ?o { <urn:a> <urn:p>+/<urn:p>+ ?o }", 16)]
    [DataRow("SELECT DISTINCT ?o { <urn:a> <urn:p>+/<urn:p>+ ?o }", 4)]
    [DataRow("SELECT ?o { <urn:a> (<urn:p>+/<urn:p>+)+ ?o }", 4)]
    [DataRow("SELECT ?o { <urn:a> (<urn:p>+|<urn:p>+) ?o }", 8)]
    [DataRow("SELECT ?o { <urn:a> (<urn:p>+|<urn:p>+)+ ?o }", 4)]
    [DataRow("SELECT ?o { VALUES ?s { <urn:a> <urn:a> } ?s <urn:p>+/<urn:p>+ ?o }", 32)]
    [DataRow("SELECT ?o { { SELECT ?s { VALUES ?s { <urn:a> <urn:a> } } } ?s (<urn:p>+/<urn:p>+)+ ?o }", 8)]
    [DataRow("SELECT ?o { <urn:a> <urn:p>+ ?o FILTER EXISTS { ?o <urn:p>+ <urn:a> } }", 4)]
    public async Task Sparql11_paths_distinguish_bags_from_distinct_reachability(string query, int expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var a = new RdfIri("urn:a");
        var b = new RdfIri("urn:b");
        var c = new RdfIri("urn:c");
        var d = new RdfIri("urn:d");
        var p = new RdfIri("urn:p");
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new AssertRdfQuad(new(a, p, b)), new AssertRdfQuad(new(a, p, c)),
            new AssertRdfQuad(new(b, p, d)), new AssertRdfQuad(new(c, p, d)),
            new AssertRdfQuad(new(d, p, a)), new AssertRdfQuad(new(a, new RdfIri("urn:q"), b)),
            new AssertRdfQuad(new(a, p, new RdfIri("urn:z"), new RdfIri("urn:g")))
        ]));
        Assert.AreEqual(expected, RdfSparql.Execute(database, query).Count);
    }

    [TestMethod]
    public async Task Sparql11_nested_reachability_fails_its_work_bound_instead_of_returning_a_prefix()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var p = new RdfIri("urn:p");
        await database.CommitAsync(new(Guid.NewGuid(), Enumerable.Range(0, 500).Select(index =>
            (RdfMutation)new AssertRdfQuad(new(new RdfIri("urn:n" + index), p, new RdfIri("urn:n" + ((index + 1) % 500))))).ToArray()));
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() =>
            RdfSparql.Execute(database, "SELECT ?o { <urn:n0> (<urn:p>+)+ ?o } LIMIT 1"));
    }
}
