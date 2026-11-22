using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Rdf;
using Microsoft.AspNetCore.Hosting;
using VDS.RDF.Parsing;
using VDS.RDF.Query;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Sparql11_form_query_combines_dataset_selectors_but_rejects_URL_query_text()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (string name in new[] { "a", "b", "excluded" })
        {
            using var seed = await client.PutAsync("/sparql/graphs?graph=urn:" + name,
                new StringContent("<urn:" + name + "> <urn:p> 1 .", Encoding.UTF8, "text/turtle"));
            Assert.IsTrue(seed.IsSuccessStatusCode);
        }
        using var result = await client.PostAsync("/sparql?default-graph-uri=urn:a", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["query"] = "SELECT (COUNT(*) AS ?count) WHERE {?s ?p ?o}",
            ["default-graph-uri"] = "urn:b"
        }));
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode, await result.Content.ReadAsStringAsync());
        var json = await result.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("2", json.GetProperty("results").GetProperty("bindings")[0].GetProperty("count").GetProperty("value").GetString());
        foreach (string parameter in new[] { "query=ASK%7B%7D", "unknown=value" })
        {
            using var rejected = await client.PostAsync("/sparql?" + parameter,
                new FormUrlEncodedContent(new Dictionary<string, string> { ["query"] = "ASK {}" }));
            Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("GET", "json")]
    [DataRow("FORM", "json")]
    [DataRow("RAW", "json")]
    [DataRow("GET", "xml")]
    [DataRow("FORM", "xml")]
    [DataRow("RAW", "xml")]
    public async Task Sparql11_protocol_results_are_readable_by_independent_parser(string transport, string format)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, readerToken: ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host, ReaderToken);
        string type = "application/sparql-results+" + format;
        foreach (string query in new[]
        {
            "SELECT ?uri ?text ?number ?missing { VALUES (?uri ?text ?number) { (<urn:a> 'A<&'@en 42) } }",
            "SELECT ?missing { ?missing <urn:absent> ?o }",
            "SELECT * { ?missing <urn:absent> ?o }",
            "ASK {}"
        })
        {
            using var request = new HttpRequestMessage(transport == "GET" ? HttpMethod.Get : HttpMethod.Post,
                transport == "GET" ? "/sparql?query=" + Uri.EscapeDataString(query) : "/sparql");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(type));
            if (transport == "FORM")
            {
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["query"] = query });
            }
            else if (transport == "RAW")
            {
                request.Content = new StringContent(query, Encoding.UTF8, "application/sparql-query");
            }
            using var response = await client.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
            Assert.AreEqual(type, response.Content.Headers.ContentType?.MediaType);
            var parsed = new SparqlResultSet();
            if (format == "json")
            {
                new SparqlJsonParser().Load(parsed, new StringReader(body));
            }
            else
            {
                new SparqlXmlParser().Load(parsed, new StringReader(body));
            }
            if (query.StartsWith("ASK", StringComparison.Ordinal))
            {
                Assert.IsTrue(parsed.Result);
            }
            else if (query.Contains("urn:absent", StringComparison.Ordinal))
            {
                Assert.AreEqual(0, parsed.Count);
                Assert.IsTrue(parsed.Variables.Contains("missing"));
                Assert.AreEqual(query.Contains('*') ? 2 : 1, parsed.Variables.Count());
            }
            else
            {
                Assert.AreEqual(1, parsed.Count);
                Assert.AreEqual(4, parsed.Variables.Count());
                Assert.AreEqual("A<&", ((VDS.RDF.ILiteralNode)parsed[0]["text"]).Value);
                Assert.AreEqual("en", ((VDS.RDF.ILiteralNode)parsed[0]["text"]).Language);
                Assert.AreEqual("42", ((VDS.RDF.ILiteralNode)parsed[0]["number"]).Value);
                Assert.AreEqual(new Uri("urn:a"), ((VDS.RDF.IUriNode)parsed[0]["uri"]).Uri);
            }
        }
    }

    [TestMethod]
    public async Task Sparql11_protocol_negotiates_graph_results_and_rejects_invalid_requests()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, readerToken: ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host, ReaderToken);
        using var graph = await client.PostAsync("/sparql", new StringContent(
            "CONSTRUCT { <urn:a> <urn:p> 'value' } WHERE {}", Encoding.UTF8, "application/sparql-query"));
        Assert.AreEqual(HttpStatusCode.OK, graph.StatusCode, await graph.Content.ReadAsStringAsync());
        Assert.AreEqual("text/turtle", graph.Content.Headers.ContentType?.MediaType);
        using var graphBody = await graph.Content.ReadAsStreamAsync();
        Assert.AreEqual(1, RdfSyntax.ParseTurtle(graphBody, "protocol").Count);
        foreach (string path in new[]
        {
            "/sparql?query=", "/sparql?query=ASK%7B%7D&query=ASK%7B%7D",
            "/sparql?query=ASK%7B%7D&default-graph-uri=relative",
            "/sparql?query=ASK%7B%7D&default-graph-uri=urn:g%3E%20WHERE%20%7B%7D",
            "/sparql?query=ASK%7B%7D&named-graph-uri=",
            "/sparql?query=ASK%7B%7D&unknown=value"
        })
        {
            using var invalid = await client.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var media = await client.PostAsJsonAsync("/sparql", new
        {
            query = "ASK {}"
        });
        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, media.StatusCode);
        using var preferred = new HttpRequestMessage(HttpMethod.Get, "/sparql?query=ASK%7B%7D");
        preferred.Headers.TryAddWithoutValidation("Accept", "APPLICATION/SPARQL-RESULTS+XML;q=0.9, application/sparql-results+json;q=0.1");
        using var negotiated = await client.SendAsync(preferred);
        Assert.AreEqual("application/sparql-results+xml", negotiated.Content.Headers.ContentType?.MediaType);
        using var openapi = await client.GetAsync("/openapi/v1.json");
        var specification = await openapi.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(specification.GetProperty("paths").GetProperty("/sparql").TryGetProperty("post", out _));
        foreach (string accept in new[] { "text/csv", "application/sparql-results+json;q=0, application/sparql-results+xml;q=0, */*;q=1" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/sparql?query=ASK%7B%7D");
            request.Headers.TryAddWithoutValidation("Accept", accept);
            using var rejected = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.NotAcceptable, rejected.StatusCode);
        }
        using var unauthorized = Client(host);
        unauthorized.DefaultRequestHeaders.Authorization = null;
        using var denied = await unauthorized.GetAsync("/sparql?query=ASK%7B%7D");
        Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var update = await client.PostAsync("/sparql", new StringContent(
            "INSERT DATA { <urn:a> <urn:p> 'no' }", Encoding.UTF8, "application/sparql-query"));
        Assert.IsFalse(update.IsSuccessStatusCode);
        using var unchanged = await client.GetAsync("/sparql?query=" + Uri.EscapeDataString("ASK { <urn:a> <urn:p> ?v }"));
        Assert.IsFalse((await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("boolean").GetBoolean());
    }
}
