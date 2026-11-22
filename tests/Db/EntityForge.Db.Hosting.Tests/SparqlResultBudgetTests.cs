using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("json", "x")]
    [DataRow("xml", "x")]
    [DataRow("json", "雪")]
    [DataRow("xml", "雪")]
    [DataRow("csv", "x")]
    [DataRow("tsv", "x")]
    [DataRow("csv", "雪")]
    [DataRow("tsv", "雪")]
    public async Task Sparql_result_serialization_rejects_oversized_documents_and_recovers(string format, string character)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        int literalLength = character == "x" ? 250_000 : 40_000;
        bool tabular = format is "csv" or "tsv";
        string media = format switch
        {
            "csv" => "text/csv",
            "tsv" => "text/tab-separated-values",
            _ => "application/sparql-results+" + format
        };
        string Query(int rows) => "SELECT ?payload { VALUES ?i { " + String.Join(" ", Enumerable.Range(0, rows)) +
            " } BIND('" + new string(character[0], literalLength) + "' AS ?payload) }";
        using var allowedRequest = new HttpRequestMessage(HttpMethod.Post, "/sparql")
        {
            Content = new StringContent(Query(8), Encoding.UTF8, "application/sparql-query")
        };
        allowedRequest.Headers.Accept.ParseAdd(media);
        using var allowed = await client.SendAsync(allowedRequest);
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode, allowed.IsSuccessStatusCode ? null : await allowed.Content.ReadAsStringAsync());
        Assert.IsTrue(allowed.Content.Headers.ContentLength is > 100_000 and < 16_777_216);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/sparql")
        {
            Content = new StringContent(Query(character == "x" ? 70 : tabular ? 500 : 200), Encoding.UTF8, "application/sparql-query")
        };
        request.Headers.Accept.ParseAdd(media);
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Length < 4096, "Oversized result must be replaced by a small problem document.");
        using var problem = JsonDocument.Parse(body);
        Assert.AreEqual("rdf_sparql_work_limit_exceeded", problem.RootElement.GetProperty("code").GetString());
        using var recovered = await client.GetAsync("/sparql?query=ASK%7B%7D");
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
    }
}
