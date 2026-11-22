using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT (BNODE()=BNODE() AS ?value) {}")]
    [DataRow("SELECT (isBlank(BNODE()) AS ?value) { VALUES ?n { 1 1 2 } }")]
    public async Task Sparql11_bnode_scalar_results_match_independent_reference(string query)
    {
        using var store = new VDS.RDF.TripleStore();
        var processor = new VDS.RDF.Query.LeviathanQueryProcessor(store);
        var reference = (VDS.RDF.Query.SparqlResultSet)processor.ProcessQuery(new VDS.RDF.Parsing.SparqlQueryParser().ParseFromString(query));
        await using var database = await OpenAsync(CreateDirectory());
        var actual = RdfSparql.Execute(database, query);
        CollectionAssert.AreEqual(reference.Select(row => ((VDS.RDF.ILiteralNode)row["value"]).Value).ToArray(),
            actual.Select(row => ((RdfLiteral)row["value"]).LexicalForm).ToArray());
    }

    [TestMethod]
    [DataRow("'x'")]
    [DataRow("'x'^^<http://www.w3.org/2001/XMLSchema#string>")]
    public async Task Sparql11_bnode_names_are_stable_per_solution_and_fresh_between_solutions(string name)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var query = $"SELECT (BNODE({name}) AS ?a) (BNODE({name}) AS ?b) (BNODE('y') AS ?c) {{ VALUES ?n {{ 1 1 2 }} }}";
        var rows = RdfSparql.Execute(database, query);
        Assert.AreEqual(3, rows.Count);
        Assert.AreEqual(3, rows.Select(row => row["a"]).Distinct().Count());
        foreach (var row in rows)
        {
            Assert.IsInstanceOfType<RdfBlankNode>(row["a"]);
            Assert.AreEqual(row["a"], row["b"]);
            Assert.AreNotEqual(row["a"], row["c"]);
        }
        var second = RdfSparql.Execute(database, query);
        Assert.IsFalse(rows.Select(row => row["a"]).Intersect(second.Select(row => row["a"])).Any());
    }

    [TestMethod]
    public async Task Sparql11_bnode_without_arguments_is_fresh_on_every_call()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var rows = RdfSparql.Execute(database, "SELECT (BNODE() AS ?a) (BNODE() AS ?b) { VALUES ?n { 1 1 } }");
        Assert.AreEqual(4, rows.SelectMany(row => row.Values).Distinct().Count());
        var count = RdfSparql.Execute(database, "SELECT (COUNT(DISTINCT BNODE('x')) AS ?count) { VALUES ?n { 1 1 2 } }").Single();
        Assert.AreEqual(new RdfLiteral("3", "http://www.w3.org/2001/XMLSchema#integer"), count["count"]);
    }

    [TestMethod]
    public async Task Sparql11_bnode_supports_bind_filters_and_construct()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, "SELECT ?a (BNODE('x') AS ?b) { BIND(BNODE('x') AS ?a) FILTER(isBlank(?a) && BNODE('x')=BNODE('x')) }").Single();
        Assert.AreEqual(row["a"], row["b"]);
        var quads = RdfSparql.ExecuteConstruct(database,
            "CONSTRUCT { ?b <urn:value> ?n } WHERE { VALUES ?n { 1 1 2 } BIND(BNODE() AS ?b) }");
        Assert.AreEqual(3, quads.Count);
        Assert.AreEqual(3, quads.Select(quad => quad.Subject).Distinct().Count());
    }

    [TestMethod]
    [DataRow("VALUES ?n { 1 1 2 }", 3)]
    [DataRow("{ VALUES ?n { 1 } } UNION { VALUES ?n { 1 } }", 2)]
    [DataRow("{ SELECT ?n (NOW() AS ?innerClock) WHERE { VALUES ?n { 1 1 2 } } }", 3)]
    [DataRow("{ SELECT ?n (COUNT(*) AS ?count) (NOW() AS ?innerClock) WHERE { VALUES ?n { 1 1 2 } } GROUP BY ?n }", 2)]
    [DataRow("VALUES ?n { 1 1 } OPTIONAL { VALUES ?other { 9 } FILTER(false) }", 2)]
    public async Task Sparql11_solution_functions_preserve_identity_after_algebra_boundaries(string pattern, int count)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string query = "SELECT ?innerClock (BNODE('same') AS ?a) (BNODE('same') AS ?b) " +
            "(NOW() AS ?clock) (NOW() AS ?again) WHERE { " + pattern + " }";
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        Assert.AreEqual(count, result.Bindings.Count);
        Assert.AreEqual(count, result.Bindings.Select(row => row["a"]).Distinct().Count(),
            "Duplicate mappings remain separate solution occurrences for blank-node construction.");
        Assert.AreEqual(1, result.Bindings.Select(row => row["clock"]).Distinct().Count());
        foreach (var row in result.Bindings)
        {
            Assert.IsInstanceOfType<RdfBlankNode>(row["a"]);
            Assert.AreEqual(row["a"], row["b"]);
            Assert.AreEqual(row["clock"], row["again"]);
            if (row.TryGetValue("innerClock", out var innerClock))
            {
                Assert.AreEqual(innerClock, row["clock"], "Subqueries share the enclosing query's clock.");
            }
        }
        var next = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        Assert.IsFalse(result.Bindings.Select(row => row["a"])
            .Intersect(next.Bindings.Select(row => row["a"])).Any());
    }

    [TestMethod]
    [DataRow("1")]
    [DataRow("<urn:x>")]
    [DataRow("'x'@en")]
    [DataRow("?missing")]
    public async Task Sparql11_bnode_rejects_nonstring_arguments_as_expression_errors(string argument)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, $"SELECT (BNODE({argument}) AS ?b) {{}}").Single();
        Assert.IsFalse(row.ContainsKey("b"));
    }
}
