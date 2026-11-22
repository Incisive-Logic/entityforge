using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_graph_store_large_reads_fail_without_mutating_data(bool unicode)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string literal = new(unicode ? '雪' : 'x', unicode ? 40_000 : 250_000);
        int total = unicode ? 200 : 70;
        const string graph = "/sparql/graphs?graph=urn%3Abudget";
        string[] formats = ["text/turtle", "application/n-triples", "application/ld+json", "application/rdf+xml"];
        for (int start = 0; start < total; start += 10)
        {
            var input = new StringBuilder();
            for (int index = start; index < start + 10; index++)
            {
                input.Append("<urn:s").Append(index).Append("> <urn:p> \"").Append(literal).Append("\" .\n");
            }
            using var written = await client.PostAsync(graph, new StringContent(input.ToString(), Encoding.UTF8, "text/turtle"));
            Assert.IsTrue(written.StatusCode is HttpStatusCode.Created or HttpStatusCode.NoContent,
                await written.Content.ReadAsStringAsync());
            if (start == 0)
            {
                foreach (string format in formats)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, graph);
                    request.Headers.Accept.ParseAdd(format);
                    using var small = await client.SendAsync(request);
                    Assert.AreEqual(HttpStatusCode.OK, small.StatusCode);
                    Assert.IsTrue(small.Content.Headers.ContentLength is > 100_000 and < 16_777_216);
                }
            }
        }
        foreach (string format in formats)
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
            {
                using var request = new HttpRequestMessage(method, graph);
                request.Headers.Accept.ParseAdd(format);
                using var response = await client.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode, format + " " + method);
                string body = await response.Content.ReadAsStringAsync();
                if (method == HttpMethod.Head)
                {
                    Assert.AreEqual(String.Empty, body);
                }
                else
                {
                    Assert.IsTrue(body.Length < 4096);
                    using var problem = JsonDocument.Parse(body);
                    Assert.AreEqual("rdf_sparql_work_limit_exceeded", problem.RootElement.GetProperty("code").GetString());
                }
            }
        }
        const string query = "SELECT (COUNT(*) AS ?count) (SUM(STRLEN(?o)) AS ?characters) { GRAPH <urn:budget> { ?s <urn:p> ?o } }";
        using var verified = await client.GetAsync("/sparql?query=" + Uri.EscapeDataString(query));
        Assert.AreEqual(HttpStatusCode.OK, verified.StatusCode);
        using var result = JsonDocument.Parse(await verified.Content.ReadAsStringAsync());
        var row = result.RootElement.GetProperty("results").GetProperty("bindings")[0];
        Assert.AreEqual(total.ToString(System.Globalization.CultureInfo.InvariantCulture), row.GetProperty("count").GetProperty("value").GetString());
        Assert.AreEqual((total * literal.Length).ToString(System.Globalization.CultureInfo.InvariantCulture), row.GetProperty("characters").GetProperty("value").GetString());
    }

    [TestMethod]
    [DataRow("text/turtle", false)]
    [DataRow("application/n-triples", false)]
    [DataRow("application/ld+json", false)]
    [DataRow("application/rdf+xml", false)]
    [DataRow("text/turtle", true)]
    [DataRow("application/n-triples", true)]
    [DataRow("application/ld+json", true)]
    [DataRow("application/rdf+xml", true)]
    public async Task Sparql_graph_serialization_rejects_oversized_documents_and_recovers(string media, bool unicode)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (int rows in new[] { 8, unicode ? 200 : 70 })
        {
            string query = "CONSTRUCT { ?s <urn:p> ?payload } WHERE { VALUES ?i { " +
                String.Join(" ", Enumerable.Range(0, rows)) + " } BIND(IRI(CONCAT('urn:s', STR(?i))) AS ?s) BIND('" +
                new string(unicode ? '雪' : 'x', unicode ? 40_000 : 250_000) + "' AS ?payload) }";
            using var request = new HttpRequestMessage(HttpMethod.Post, "/sparql")
            {
                Content = new StringContent(query, Encoding.UTF8, "application/sparql-query")
            };
            request.Headers.Accept.ParseAdd(media);
            using var response = await client.SendAsync(request);
            if (rows == 8)
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync());
                Assert.IsTrue(response.Content.Headers.ContentLength is > 100_000 and < 16_777_216);
                continue;
            }
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            string body = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(body.Length < 4096);
            using var problem = JsonDocument.Parse(body);
            Assert.AreEqual("rdf_sparql_work_limit_exceeded", problem.RootElement.GetProperty("code").GetString());
        }
        using var recovered = await client.GetAsync("/sparql?query=ASK%7B%7D");
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
    }
}
