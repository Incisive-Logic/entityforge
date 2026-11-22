using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("<urn:a> <urn:p> ?v . {} BIND(2*?v AS ?twice)", "2")]
    [DataRow("<urn:a> <urn:p> ?v . BIND(2*?v AS ?twice) {}", "2")]
    [DataRow("BIND(2*?v AS ?twice) <urn:a> <urn:p> ?v", null)]
    [DataRow("BIND(2*?v AS ?twice) {} <urn:a> <urn:p> ?v", null)]
    [DataRow("<urn:a> <urn:p> ?v . { BIND(2*?v AS ?twice) }", null)]
    public async Task Sparql_bind_respects_lexical_order_and_nested_group_scope(string body, string? expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:a> <urn:p> 1 }");
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync("SELECT ?v ?twice { " + body + " }");
        Assert.AreEqual(1, result.Bindings.Count);
        var row = result.Bindings.Single();
        Assert.AreEqual("1", ((RdfLiteral)row["v"]).LexicalForm);
        if (expected is null)
        {
            Assert.IsFalse(row.ContainsKey("twice"));
        }
        else
        {
            Assert.AreEqual(expected, ((RdfLiteral)row["twice"]).LexicalForm);
        }
    }

    [TestMethod]
    [DataRow("false", false, null)]
    [DataRow("1/0 > 0", false, null)]
    [DataRow("?v=2 || 1/0 > 0", false, "2")]
    [DataRow("?v=1 || ?v=2", false, "1,2")]
    [DataRow("false", true, null)]
    [DataRow("1/0 > 0", true, null)]
    [DataRow("?v=2 || 1/0 > 0", true, "2")]
    [DataRow("?v=1 || ?v=2", true, "1,2")]
    public async Task Sparql_optional_false_and_error_filters_preserve_outer_bag(string filter, bool remote, string? expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        server.MapPost("/sparql", () =>
        {
            Interlocked.Increment(ref fetched);
            return Results.Text(ServiceRows, "application/sparql-results+json");
        });
        if (remote)
        {
            await server.StartAsync();
        }
        var endpoint = new Uri(remote ? server.Urls.Single() + "/sparql" : "http://127.0.0.1:1/sparql");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:a> <urn:p> 1 . <urn:b> <urn:p> 2 }");
        string candidates = remote ? "SERVICE <" + endpoint.AbsoluteUri + "> { ?s <urn:p> ?v }" : "?s <urn:p> ?v";
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
            "SELECT ?outer ?s ?v { VALUES ?outer { 7 7 } OPTIONAL { " + candidates + " FILTER(" + filter + ") } }");
        var actual = result.Bindings.Select(row =>
        {
            Assert.AreEqual("7", ((RdfLiteral)row["outer"]).LexicalForm);
            if (!row.TryGetValue("v", out var value))
            {
                Assert.IsFalse(row.ContainsKey("s"));
                return "unbound";
            }
            string lexical = ((RdfLiteral)value).LexicalForm;
            Assert.AreEqual(new RdfIri(lexical == "1" ? "urn:a" : "urn:b"), row["s"]);
            return lexical;
        }).ToArray();
        string[] values = expected?.Split(',') ?? ["unbound"];
        CollectionAssert.AreEquivalent(values.Concat(values).ToArray(), actual);
        Assert.AreEqual(remote ? 1 : 0, fetched);
    }
}
