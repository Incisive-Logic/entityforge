using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("(2) IN (2)", "true")]
    [DataRow("((2)) not in (3)", "true")]
    [DataRow("(1+1) IN (2)", "true")]
    [DataRow("(1+1)*2 IN (4)", "true")]
    [DataRow("(true) IN (true)", "true")]
    [DataRow("(true && false) NOT IN (false)", "false")]
    [DataRow("(2) IN ()", "false")]
    [DataRow("(2) NOT IN ()", "true")]
    [DataRow("?missing IN ()", null)]
    [DataRow("?missing NOT IN ()", null)]
    [DataRow("(1/0) IN ()", null)]
    [DataRow("(1/0) NOT IN ()", null)]
    [DataRow("COALESCE(?missing IN (), true)", "true")]
    [DataRow("COALESCE(?missing NOT IN (), false)", "false")]
    [DataRow("COALESCE(?missing, 2) IN ()", "false")]
    [DataRow("COALESCE(?missing, 2) NOT IN ()", "true")]
    [DataRow("((2)) in (3,1/0)", null)]
    [DataRow("!(2 IN (3))", "true")]
    public async Task Sparql_parenthesized_membership_preserves_precedence_and_error_contexts(string expression, string? expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        foreach (string query in new[]
        {
            "SELECT (" + expression + " AS ?answer) WHERE {}",
            "SELECT ?answer WHERE { BIND(" + expression + " AS ?answer) }"
        })
        {
            var row = RdfSparql.Execute(database, query).Single();
            Assert.AreEqual(expected is null ? 0 : 1, row.Count, query);
            if (expected is not null)
            {
                Assert.AreEqual(new RdfLiteral(expected, "http://www.w3.org/2001/XMLSchema#boolean"), row["answer"], query);
            }
        }
        Assert.AreEqual(expected == "true" ? 2 : 0, RdfSparql.Execute(database,
            "SELECT ?seed WHERE { VALUES ?seed { 7 7 } FILTER(" + expression + ") }").Count, expression);
    }

    [TestMethod]
    [DataRow("1/0, 2", "true")]
    [DataRow("2, 1/0", "true")]
    [DataRow("3, 1/0", null)]
    [DataRow("?missing, 2", "true")]
    [DataRow("2, ?missing", "true")]
    [DataRow("3, ?missing", null)]
    [DataRow("<urn:other>, 'text', 2.0", "true")]
    [DataRow("3, 4", "false")]
    [DataRow("", "false")]
    public async Task Sparql_membership_errors_are_masked_only_by_a_match(string candidates, string? expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        foreach (bool negated in new[] { false, true })
        {
            string expression = "2 " + (negated ? "NOT IN" : "IN") + " (" + candidates + ")";
            string? value = expected is null ? null : negated ? (expected == "true" ? "false" : "true") : expected;
            var projected = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
                "SELECT ?marker (" + expression + " AS ?result) { VALUES ?marker { 7 7 } }");
            Assert.AreEqual(2, projected.Bindings.Count);
            foreach (var row in projected.Bindings)
            {
                Assert.AreEqual(new RdfLiteral("7", "http://www.w3.org/2001/XMLSchema#integer"), row["marker"]);
                if (value is null)
                {
                    Assert.IsFalse(row.ContainsKey("result"));
                }
                else
                {
                    Assert.AreEqual(new RdfLiteral(value, "http://www.w3.org/2001/XMLSchema#boolean"), row["result"]);
                }
            }
            var filtered = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
                "SELECT ?marker { VALUES ?marker { 7 7 } FILTER(" + expression + ") }");
            Assert.AreEqual(value == "true" ? 2 : 0, filtered.Bindings.Count);
        }
    }

    [TestMethod]
    [DataRow("$error, 2", "true")]
    [DataRow("2, $error", "true")]
    [DataRow("3, $error", null)]
    [DataRow("$error, 3", null)]
    [DataRow("$value, 3", "true")]
    [DataRow("3, $value", "true")]
    public async Task Sparql_federated_membership_preserves_comparison_errors_across_contexts(string candidates, string? expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapPost("/sparql", () => Results.Text(ServiceRows, "application/sparql-results+json"));
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        string exists = "EXISTS { SERVICE <" + endpoint.AbsoluteUri + "> { ?s <urn:p> ?v } }";
        string values = candidates.Replace("$error", "IF(" + exists + ", 1/0, 3)", StringComparison.Ordinal)
            .Replace("$value", "IF(" + exists + ", 2, 3)", StringComparison.Ordinal);
        foreach (bool negated in new[] { false, true })
        {
            string expression = "2 " + (negated ? "NOT IN" : "IN") + " (" + values + ")";
            string? value = expected is null ? null : negated ? (expected == "true" ? "false" : "true") : expected;
            foreach (string query in new[]
            {
                "SELECT ?marker (" + expression + " AS ?result) { VALUES ?marker { 7 7 } }",
                "SELECT ?marker ?result { VALUES ?marker { 7 7 } BIND(" + expression + " AS ?result) }"
            })
            {
                var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
                Assert.AreEqual(2, result.Bindings.Count, query);
                foreach (var row in result.Bindings)
                {
                    Assert.AreEqual(value is not null, row.ContainsKey("result"), query);
                    if (value is not null)
                    {
                        Assert.AreEqual(new RdfLiteral(value, "http://www.w3.org/2001/XMLSchema#boolean"), row["result"], query);
                    }
                }
            }
            var filtered = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
                "SELECT ?marker { VALUES ?marker { 7 7 } FILTER(" + expression + ") }");
            Assert.AreEqual(value == "true" ? 2 : 0, filtered.Bindings.Count, expression);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Sparql_empty_membership_preserves_federated_operand_errors(bool negated, bool recover)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapPost("/sparql", () => Results.Text(ServiceRows, "application/sparql-results+json"));
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        string operand = "IF(EXISTS { SERVICE <" + endpoint.AbsoluteUri + "> { ?s <urn:p> ?v } }, ?missing, 2)";
        string expression = operand + (negated ? " NOT IN ()" : " IN ()");
        if (recover)
        {
            expression = "COALESCE(" + expression + ", true)";
        }
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
            "SELECT (" + expression + " AS ?answer) {}");
        var row = result.Bindings.Single();
        Assert.AreEqual(recover, row.ContainsKey("answer"));
        if (recover)
        {
            Assert.AreEqual(new RdfLiteral("true", "http://www.w3.org/2001/XMLSchema#boolean"), row["answer"]);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_membership_evaluates_federated_left_operand_once(bool negated)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int requests = 0;
        server.MapPost("/sparql", () =>
        {
            Interlocked.Increment(ref requests);
            return Results.Text(ServiceRows, "application/sparql-results+json");
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        string operand = "IF(EXISTS { SERVICE <" + endpoint.AbsoluteUri + "> { ?s <urn:p> ?v } }, 2, 3)";
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
            "SELECT (" + operand + (negated ? " NOT IN " : " IN ") + "(0, 1, 2) AS ?result) {}");
        Assert.AreEqual(1, result.Bindings.Count);
        Assert.AreEqual(new RdfLiteral(negated ? "false" : "true", "http://www.w3.org/2001/XMLSchema#boolean"),
            result.Bindings[0]["result"]);
        Assert.AreEqual(1, requests, "The left operand must not be reevaluated for each comparison.");
    }
}
