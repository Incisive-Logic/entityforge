using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public async Task Sparql11_SERVICE_XML_preserves_terms_bags_and_response_blank_scopes()
    {
        const string row = """
            <result><binding name="b"><bnode>same</bnode></binding>
            <binding name="s"><uri>urn:subject</uri></binding>
            <binding name="label"><literal xml:lang="en"> A &amp; B </literal></binding>
            <binding name="n"><literal datatype="http://www.w3.org/2001/XMLSchema#integer">42</literal></binding>
            <binding name="empty"><literal></literal></binding></result>
            """;
        string body = """
            <sparql xmlns="http://www.w3.org/2005/sparql-results#"><head>
            <variable name="b"/><variable name="s"/><variable name="label"/><variable name="n"/>
            <variable name="empty"/><variable name="unbound"/><link href="metadata.rdf"/>
            </head><results>
            """ + row + row + "</results></sparql>";
        await WithXmlService(body, async (database, endpoint) =>
        {
            string service = $"SERVICE <{endpoint}> {{ ?b ?s ?label }}";
            var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync($"SELECT * {{ {{ {service} }} UNION {{ {service} }} }}");
            Assert.AreEqual(4, result.Bindings.Count);
            Assert.AreEqual(2, result.Bindings.Select(binding => binding["b"]).Distinct().Count());
            foreach (var binding in result.Bindings)
            {
                Assert.IsInstanceOfType<RdfBlankNode>(binding["b"]);
                Assert.AreEqual(new RdfIri("urn:subject"), binding["s"]);
                Assert.AreEqual(new RdfLiteral(" A & B ", language: "en"), binding["label"]);
                Assert.AreEqual(new RdfLiteral("42", "http://www.w3.org/2001/XMLSchema#integer"), binding["n"]);
                Assert.AreEqual(new RdfLiteral(""), binding["empty"]);
                Assert.IsFalse(binding.ContainsKey("unbound"));
            }
        });
    }

    [TestMethod]
    [DataRow("<head/><boolean>true</boolean>")]
    [DataRow("<head><variable name='x'/></head><results><result><binding name='x'><literal xml:lang='123'>x</literal></binding></result></results>")]
    [DataRow("<head><variable name='x'/></head><results><result><binding name='x'><literal xml:lang='en--GB'>x</literal></binding></result></results>")]
    [DataRow("<head><variable name='x'/><variable name='x'/></head><results/>")]
    [DataRow("<head/><results><result><binding name='x'><uri>urn:a</uri></binding></result></results>")]
    [DataRow("<head><variable name='x'/></head><results><result><binding name='x'><uri>urn:a</uri></binding><binding name='x'><uri>urn:b</uri></binding></result></results>")]
    [DataRow("<head><variable name='x'/></head><results><result><binding name='x'><literal xml:lang='en' datatype='urn:d'>x</literal></binding></result></results>")]
    [DataRow("<head/><results><result>unexpected text</result></results>")]
    [DataRow("<head/><results><result><binding name='x'/></result></results>")]
    public async Task Sparql11_SERVICE_XML_invalid_results_obey_SILENT(string content)
    {
        await AssertXmlServiceFailure("<sparql xmlns='http://www.w3.org/2005/sparql-results#'>" + content + "</sparql>");
    }

    [TestMethod]
    public async Task Sparql11_SERVICE_XML_rejects_DTD_and_wrong_namespace()
    {
        await AssertXmlServiceFailure("<!DOCTYPE sparql [<!ENTITY external SYSTEM 'file:///nonexistent-entityforge-test'>]><sparql xmlns='http://www.w3.org/2005/sparql-results#'><head/><results>&external;</results></sparql>");
        await AssertXmlServiceFailure("<sparql xmlns='urn:wrong'><head/><results/></sparql>");
        await AssertXmlServiceFailure("<sparql");
    }

    [TestMethod]
    public async Task Sparql11_SERVICE_XML_empty_success_is_not_SILENT_failure()
    {
        await WithXmlService("<sparql xmlns='http://www.w3.org/2005/sparql-results#'><head/><results/></sparql>", async (database, endpoint) =>
        {
            var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync($"SELECT * {{ SERVICE SILENT <{endpoint}> {{ ?s ?p ?o }} }}");
            Assert.AreEqual(0, result.Bindings.Count);
        });
    }

    [TestMethod]
    [DataRow("rows")]
    [DataRow("columns")]
    [DataRow("depth")]
    public async Task Sparql11_SERVICE_XML_hard_limits_are_not_silenced(string limit)
    {
        string content = limit switch
        {
            "rows" => "<head/><results>" + String.Concat(Enumerable.Repeat("<result/>", 10001)) + "</results>",
            "columns" => "<head>" + String.Concat(Enumerable.Range(0, 257).Select(i => $"<variable name='v{i}'/>")) + "</head><results/>",
            _ => String.Concat(Enumerable.Repeat("<nested>", 18)) + String.Concat(Enumerable.Repeat("</nested>", 18))
        };
        await WithXmlService("<sparql xmlns='http://www.w3.org/2005/sparql-results#'>" + content + "</sparql>", async (database, endpoint) =>
            await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(() => database.QuerySparqlAsync($"SELECT * {{ SERVICE SILENT <{endpoint}> {{ ?s ?p ?o }} }}").AsTask()));
    }

    private async Task AssertXmlServiceFailure(string body) => await WithXmlService(body, async (database, endpoint) =>
    {
        await Assert.ThrowsExactlyAsync<RdfSparqlRemoteExecutionException>(() => database.QuerySparqlAsync($"SELECT * {{ SERVICE <{endpoint}> {{ ?s ?p ?o }} }}").AsTask());
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync($"SELECT * {{ SERVICE SILENT <{endpoint}> {{ ?s ?p ?o }} }}");
        Assert.AreEqual(1, result.Bindings.Count);
        Assert.AreEqual(0, result.Bindings[0].Count);
    });

    private async Task WithXmlService(string body, Func<IRdfDatabase, Uri, Task> check)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapPost("/sparql", (HttpContext context) =>
        {
            Assert.IsTrue(context.Request.Headers.Accept.ToString().Contains("application/sparql-results+xml", StringComparison.Ordinal));
            return Results.Text(body, "application/sparql-results+xml");
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        await check(database, endpoint);
        await server.StopAsync();
    }
}
