using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    private const string ComparisonPrefix = "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> ";

    [TestMethod]
    public async Task Sparql_identical_unknown_literals_retain_term_equality()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, "SELECT (?a=?b AS ?equal) (sameTerm(?a,?b) AS ?same) " +
            "(?a IN (?b) AS ?included) WHERE { VALUES (?a ?b) { ('value'^^<urn:custom> 'value'^^<urn:custom>) } }").Single();
        Assert.AreEqual(3, row.Count);
        foreach (var value in row.Values)
        {
            Assert.AreEqual(new RdfLiteral("true", "http://www.w3.org/2001/XMLSchema#boolean"), value);
        }
    }

    [TestMethod]
    [DataRow("'text'")]
    [DataRow("true")]
    [DataRow("'2026-11-18T00:00:00Z'^^xsd:dateTime")]
    [DataRow("'value'^^<urn:custom>")]
    [DataRow("'invalid'^^xsd:integer")]
    [DataRow("'invalid'^^xsd:double")]
    public async Task Sparql_nan_does_not_mask_comparison_operand_errors(string other)
    {
        await using var database = await OpenAsync(CreateDirectory());
        foreach (bool reverse in new[] { false, true })
        {
            string left = reverse ? other : "'NaN'^^xsd:double";
            string right = reverse ? "'NaN'^^xsd:double" : other;
            foreach (string operation in new[] { "=", "!=", "<", "<=", ">", ">=" })
            {
                string expression = left + operation + right;
                var row = RdfSparql.Execute(database, ComparisonPrefix + "SELECT (" + expression + " AS ?value) WHERE {}").Single();
                Assert.AreEqual(0, row.Count, expression);
                Assert.AreEqual(0, RdfSparql.Execute(database, ComparisonPrefix +
                    "SELECT * WHERE { FILTER(!(" + expression + ")) }").Count, expression);
                Assert.AreEqual(1, RdfSparql.Execute(database, ComparisonPrefix +
                    "SELECT * WHERE { FILTER(COALESCE(" + expression + ",true)) }").Count, expression);
            }
            var membership = RdfSparql.Execute(database, ComparisonPrefix + "SELECT (" + left + " IN (" + right + ") AS ?included) " +
                "(" + left + " NOT IN (" + right + ") AS ?excluded) WHERE {}").Single();
            Assert.AreEqual(0, membership.Count, left + " IN " + right);
        }
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("1.25")]
    [DataRow("'INF'^^xsd:float")]
    [DataRow("'NaN'^^xsd:double")]
    public async Task Sparql_nan_keeps_unordered_numeric_comparison_semantics(string other)
    {
        await using var database = await OpenAsync(CreateDirectory());
        foreach (bool reverse in new[] { false, true })
        {
            string left = reverse ? other : "'NaN'^^xsd:double";
            string right = reverse ? "'NaN'^^xsd:double" : other;
            foreach (string operation in new[] { "=", "!=", "<", "<=", ">", ">=" })
            {
                string expression = left + operation + right;
                var row = RdfSparql.Execute(database, ComparisonPrefix + "SELECT (" + expression + " AS ?value) WHERE {}").Single();
                Assert.AreEqual(new RdfLiteral(operation == "!=" ? "true" : "false",
                    "http://www.w3.org/2001/XMLSchema#boolean"), row["value"], expression);
            }
        }
    }

    [TestMethod]
    [DataRow("integer")]
    [DataRow("decimal")]
    [DataRow("float")]
    [DataRow("double")]
    [DataRow("boolean")]
    [DataRow("dateTime")]
    public async Task Sparql_identical_ill_typed_literals_retain_term_equality_but_not_ordering(string datatype)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string value = "'invalid'^^xsd:" + datatype;
        var row = RdfSparql.Execute(database, ComparisonPrefix + "SELECT (?a=?b AS ?equal) (?a!=?b AS ?different) " +
            "(sameTerm(?a,?b) AS ?same) (?a IN (?b) AS ?included) (?a NOT IN (?b) AS ?excluded) (?a<?b AS ?less) " +
            "WHERE { VALUES (?a ?b) { (" + value + " " + value + ") } }").Single();
        Assert.AreEqual(5, row.Count);
        foreach (string name in new[] { "same", "equal", "included", "different", "excluded" })
        {
            Assert.AreEqual(new RdfLiteral(name is "different" or "excluded" ? "false" : "true",
                "http://www.w3.org/2001/XMLSchema#boolean"), row[name], name);
        }
    }

    [TestMethod]
    [DataRow("= 'text'")]
    [DataRow("!= 'text'")]
    [DataRow("< 'text'")]
    [DataRow("IN ('text')")]
    [DataRow("NOT IN ('text')")]
    public async Task Sparql_nan_operand_validation_applies_to_federated_expressions(string operation)
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
        string operand = "IF(EXISTS { SERVICE <" + endpoint.AbsoluteUri + "> { ?s <urn:p> ?v } }, 'NaN'^^xsd:double, 0)";
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(ComparisonPrefix +
            "SELECT ((" + operand + ") " + operation + " AS ?result) WHERE {}");
        Assert.AreEqual(0, result.Bindings.Single().Count);
        Assert.AreEqual(1, requests);
    }

    [TestMethod]
    public async Task Sparql_nan_and_iri_equality_uses_term_comparison_but_ordering_is_an_error()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, ComparisonPrefix + "SELECT (?n=<urn:x> AS ?equal) (?n!=<urn:x> AS ?different) " +
            "(?n < <urn:x> AS ?less) (<urn:x> >= ?n AS ?greater) WHERE { BIND('NaN'^^xsd:double AS ?n) }").Single();
        Assert.AreEqual(2, row.Count);
        Assert.AreEqual(new RdfLiteral("false", "http://www.w3.org/2001/XMLSchema#boolean"), row["equal"]);
        Assert.AreEqual(new RdfLiteral("true", "http://www.w3.org/2001/XMLSchema#boolean"), row["different"]);
    }
}
