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
    public async Task Sparql_select_alias_order_is_preserved_after_service(bool aggregateFirst, bool nested)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        server.MapPost("/sparql", () =>
        {
            Interlocked.Increment(ref fetched);
            return Results.Text("""
                {"head":{"vars":["v"]},"results":{"bindings":[
                  {"v":{"type":"literal","datatype":"http://www.w3.org/2001/XMLSchema#integer","value":"1"}},
                  {"v":{"type":"literal","datatype":"http://www.w3.org/2001/XMLSchema#integer","value":"2"}}
                ]}}
                """, "application/sparql-results+json");
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        string projection = aggregateFirst
            ? "(COUNT(*) AS ?c) (EXISTS { VALUES ?c { 1 } } AS ?x)"
            : "(EXISTS { VALUES ?c { 1 } } AS ?x) (COUNT(*) AS ?c)";
        string query = "SELECT " + projection + " { SERVICE <" + endpoint.AbsoluteUri + "> { VALUES ?v { 1 2 } } }";
        if (nested)
        {
            query = "SELECT ?x ?c { { " + query + " } }";
        }
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        Assert.AreEqual(1, result.Bindings.Count);
        Assert.AreEqual("2", ((RdfLiteral)result.Bindings[0]["c"]).LexicalForm);
        Assert.AreEqual(aggregateFirst ? "false" : "true", ((RdfLiteral)result.Bindings[0]["x"]).LexicalForm);
        Assert.AreEqual(2, result.Bindings[0].Count);
        Assert.IsTrue(fetched > 0);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_select_aggregate_alias_can_feed_later_scan_expression(bool scan)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:a> <urn:p> 1 . <urn:b> <urn:p> 2 }");
        const string query = "SELECT (COUNT(*) AS ?c) (?c + 1 AS ?next) (SUM(?v) AS ?total) (?total + ?next AS ?combined) { ?s <urn:p> ?v } ORDER BY ?combined";
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
        Assert.AreEqual("6", ((RdfLiteral)rows[0]["combined"]).LexicalForm);
        Assert.AreEqual(4, rows[0].Count);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, false, true)]
    [DataRow(true, true, true)]
    public async Task Sparql_select_exists_observes_aggregate_alias_only_after_its_declaration(
        bool asynchronous, bool aggregateFirst, bool nested)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string projection = aggregateFirst
            ? "(COUNT(*) AS ?c) (EXISTS { VALUES ?c { 1 } } AS ?x)"
            : "(EXISTS { VALUES ?c { 1 } } AS ?x) (COUNT(*) AS ?c)";
        string query = "SELECT " + projection + " { VALUES ?v { 1 2 } }";
        if (nested)
        {
            query = "SELECT ?x ?c { { " + query + " } }";
        }
        var rows = asynchronous
            ? ((RdfSparqlSelectResult)await database.QuerySparqlAsync(query)).Bindings
            : RdfSparql.Execute(database, query);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("2", ((RdfLiteral)rows[0]["c"]).LexicalForm);
        Assert.AreEqual(aggregateFirst ? "false" : "true", ((RdfLiteral)rows[0]["x"]).LexicalForm);
        Assert.AreEqual(2, rows[0].Count);
    }
}
