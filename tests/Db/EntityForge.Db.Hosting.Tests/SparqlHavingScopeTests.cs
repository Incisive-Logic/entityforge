using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Sparql_having_rejects_select_aggregate_aliases(bool scan, bool computed)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:a> <urn:p> 1 . <urn:b> <urn:p> 1 }");
        string projection = computed ? "COUNT(*) + 0" : "COUNT(*)";
        string query = "SELECT ?v (" + projection + " AS ?c) { ?s <urn:p> ?v } GROUP BY ?v HAVING (?c > 1)";
        if (scan)
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            {
                await foreach (var page in RdfSparql.ScanAsync(query, database.QueryPageAsync,
                    database.ReleaseQueryContinuationAsync, new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
                {
                    _ = page;
                }
            });
        }
        else
        {
            Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database, query));
        }
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    public async Task Sparql_having_alias_scope_is_enforced_inside_subqueries()
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database,
            "SELECT ?c { { SELECT (COUNT(*) AS ?c) { VALUES ?v { 1 1 } } HAVING (?c > 1) } }"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_having_keeps_group_keys_and_aggregates_visible_before_select_aliases(bool scan)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:a> <urn:p> 1 . <urn:b> <urn:p> 1 . <urn:c> <urn:p> 2 }");
        const string query = "SELECT ?v (COUNT(*) AS ?c) (?c + 1 AS ?next) { ?s <urn:p> ?v } GROUP BY ?v HAVING (COUNT(*) > 1) (?v = 1) ORDER BY DESC(?c)";
        var rows = new List<IReadOnlyDictionary<string, RdfTerm>>();
        if (scan)
        {
            await foreach (var page in RdfSparql.ScanAsync(query, database.QueryPageAsync,
                database.ReleaseQueryContinuationAsync, new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
            {
                rows.AddRange(page.Rows);
            }
        }
        else
        {
            rows.AddRange(RdfSparql.Execute(database, query));
        }
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("1", ((RdfLiteral)rows[0]["v"]).LexicalForm);
        Assert.AreEqual("2", ((RdfLiteral)rows[0]["c"]).LexicalForm);
        Assert.AreEqual("3", ((RdfLiteral)rows[0]["next"]).LexicalForm);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Sparql_having_exists_does_not_correlate_select_aggregate_alias(bool asynchronous, bool subquery)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:a> <urn:p> 1 . <urn:b> <urn:p> 1 }");
        string query = "SELECT (COUNT(*) AS ?c) { ?s <urn:p> ?v } HAVING EXISTS { VALUES ?c { 1 } }";
        if (subquery)
        {
            query = "SELECT ?c { { " + query + " } }";
        }
        var rows = asynchronous
            ? ((RdfSparqlSelectResult)await database.QuerySparqlAsync(query)).Bindings
            : RdfSparql.Execute(database, query);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("2", ((RdfLiteral)rows[0]["c"]).LexicalForm);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_having_service_exists_does_not_correlate_select_alias(bool subquery)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        server.MapPost("/sparql", () =>
        {
            Interlocked.Increment(ref fetched);
            return Results.Text("""
                {"head":{"vars":["c"]},"results":{"bindings":[{"c":{"type":"literal","datatype":"http://www.w3.org/2001/XMLSchema#integer","value":"1"}}]}}
                """, "application/sparql-results+json");
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:a> <urn:p> 1 . <urn:b> <urn:p> 1 }");
        string query = "SELECT (COUNT(*) AS ?c) { ?s <urn:p> ?v } HAVING EXISTS { SERVICE <" +
            endpoint.AbsoluteUri + "> { VALUES ?c { 1 } } }";
        if (subquery)
        {
            query = "SELECT ?c { { " + query + " } }";
        }
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        Assert.AreEqual(1, result.Bindings.Count);
        Assert.AreEqual("2", ((RdfLiteral)result.Bindings[0]["c"]).LexicalForm);
        Assert.IsTrue(fetched > 0);
    }

    [TestMethod]
    [DataRow("BOUND(?v) (COUNT(*) > 1)", "1")]
    [DataRow("(COUNT(*) > 1) BOUND(?v)", "1")]
    [DataRow("EXISTS { FILTER(?v = 1) } BOUND(?v)", "1")]
    [DataRow("NOT EXISTS { FILTER(?v = 2) }", "1")]
    [DataRow("<http://www.w3.org/2001/XMLSchema#boolean>(?v)", "1,2")]
    [DataRow("COUNT(*)", "1,2")]
    public async Task Sparql_having_accepts_standard_constraint_forms(string condition, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:a> <urn:p> 1 . <urn:b> <urn:p> 1 . <urn:c> <urn:p> 2 }");
        var rows = RdfSparql.Execute(database, "SELECT ?v (COUNT(*) AS ?c) { ?s <urn:p> ?v } GROUP BY ?v HAVING " + condition + " ORDER BY ?v");
        Assert.AreEqual(expected, String.Join(',', rows.Select(row => ((RdfLiteral)row["v"]).LexicalForm)));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("?v")]
    [DataRow("!BOUND(?v)")]
    [DataRow("BOUND(?v) && BOUND(?v)")]
    [DataRow("EXISTS {} || EXISTS {}")]
    public async Task Sparql_having_rejects_expressions_outside_constraint_grammar(string condition)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database,
            "SELECT ?v { VALUES ?v {1} } GROUP BY ?v HAVING " + condition));
    }

    [TestMethod]
    public async Task Sparql_having_subquery_serialization_preserves_scope_at_independent_endpoint()
    {
        // The reference must contain <urn:a> <urn:p> 1 and <urn:b> <urn:p> 2.
        // Its grouped HAVING EXISTS implementation must preserve local variable scope.
        string? configured = Environment.GetEnvironmentVariable("ENTITYFORGE_SPARQL_HAVING_REFERENCE_ENDPOINT");
        if (String.IsNullOrWhiteSpace(configured))
        {
            Assert.Inconclusive("Set ENTITYFORGE_SPARQL_HAVING_REFERENCE_ENDPOINT to an independent SPARQL service with the documented fixture.");
        }
        var endpoint = new Uri(configured);
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        string query = "SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <" + endpoint.AbsoluteUri +
            "> { { SELECT (COUNT(*) AS ?n) { ?s <urn:p> ?o FILTER(?o=?x) } " +
            "HAVING EXISTS { VALUES ?n {2} FILTER(?n=2) } } FILTER(?n=1) } } } ORDER BY ?v";
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        CollectionAssert.AreEqual(new[] { "1", "2" },
            result.Bindings.Select(row => ((RdfLiteral)row["v"]).LexicalForm).ToArray());
    }
}
