using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql11_SERVICE_SILENT_composition_preserves_bags_and_distinguishes_empty_success(bool remoteFailure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapPost("/sparql", () => remoteFailure
            ? Results.StatusCode(503)
            : Results.Text("""{"head":{"vars":["s","v"]},"results":{"bindings":[]}}""", "application/sparql-results+json"));
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        string service = $"SERVICE SILENT <{endpoint}> {{ ?s <urn:p> ?v }}";
        string values = "VALUES ?x { 1 1 2 }";
        (string Body, string[] Expected)[] cases =
        [
            ($"{values} {service}", remoteFailure ? ["1", "1", "2"] : []),
            ($"{values} OPTIONAL {{ {service} }}", ["1", "1", "2"]),
            ($"{{ {values} {service} }} UNION {{ BIND(3 AS ?x) }}", remoteFailure ? ["1", "1", "2", "3"] : ["3"]),
            ($"{values} FILTER EXISTS {{ {service} }}", remoteFailure ? ["1", "1", "2"] : []),
            ($"{values} FILTER NOT EXISTS {{ {service} }}", remoteFailure ? [] : ["1", "1", "2"])
        ];
        foreach (var (body, expected) in cases)
        {
            var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync($"SELECT ?x ?s ?v {{ {body} }} ORDER BY ?x");
            var actual = result.Bindings.Select(row => ((RdfLiteral)row["x"]).LexicalForm).ToArray();
            CollectionAssert.AreEqual(expected, actual, body);
            Assert.IsTrue(result.Bindings.All(row => row.Count == 1), "Failure identity and empty success must not invent remote bindings.");
        }
        await server.StopAsync();
    }
}
