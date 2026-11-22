using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Rdf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("query", "http")]
    [DataRow("query", "payload")]
    [DataRow("query", "media")]
    [DataRow("query", "denied")]
    [DataRow("update", "http")]
    [DataRow("update", "payload")]
    [DataRow("update", "media")]
    [DataRow("update", "denied")]
    [DataRow("load", "http")]
    [DataRow("load", "payload")]
    [DataRow("load", "media")]
    [DataRow("load", "denied")]
    [DataRow("query", "budget")]
    [DataRow("update", "budget")]
    [DataRow("load", "budget")]
    public async Task Sparql11_protocol_remote_failures_are_execution_errors_with_atomic_silent_semantics(string operation, string mode)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var remote = builder.Build();
        int fetched = 0;
        remote.MapMethods("/resource", ["GET", "POST"], () =>
        {
            fetched++;
            if (mode == "budget")
            {
                return Results.Bytes(new byte[4 * 1024 * 1024 + 1], operation == "load" ? "text/turtle" : "application/sparql-results+json");
            }
            return mode == "http" ? (IResult)Results.StatusCode(503) :
                Results.Text(mode == "payload" ? "invalid remote payload" : "private remote details",
                    mode == "media" ? "application/octet-stream" : operation == "load" ? "text/turtle" : "application/sparql-results+json");
        });
        await remote.StartAsync();
        var endpoint = new Uri(remote.Urls.Single() + "/resource");
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseRdfHttpResources(new(mode == "denied" ? [] : new[] { endpoint }, allowPrivateNetwork: true))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string service = $"SERVICE <{endpoint}> {{ ?s <urn:p> ?o }}";
        string statement = operation switch
        {
            "query" => "SELECT * { " + service + " }",
            "update" => "INSERT DATA { <urn:seed> <urn:p> 1 }; INSERT { ?s <urn:copy> ?o } WHERE { " + service + " }",
            _ => $"INSERT DATA {{ <urn:seed> <urn:p> 1 }}; LOAD <{endpoint}>"
        };
        string path = operation == "query" ? "/sparql" : "/sparql/update";
        string media = operation == "query" ? "application/sparql-query" : "application/sparql-update";
        using var response = await client.PostAsync(path, new StringContent(statement, Encoding.UTF8, media));
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(mode == "budget" ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.InternalServerError, response.StatusCode, body);
        using var error = JsonDocument.Parse(body);
        Assert.AreEqual(mode == "budget" ? "rdf_sparql_work_limit_exceeded" : "rdf_sparql_remote_failure", error.RootElement.GetProperty("code").GetString());
        Assert.IsFalse(error.RootElement.GetProperty("retryable").GetBoolean());
        Assert.IsFalse(body.Contains("private remote details", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains(endpoint.AbsoluteUri, StringComparison.Ordinal));
        using var empty = await client.GetAsync("/sparql?query=" + Uri.EscapeDataString("ASK { ?s ?p ?o }"));
        Assert.AreEqual(HttpStatusCode.OK, empty.StatusCode);
        Assert.IsFalse((await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("boolean").GetBoolean());
        string silent = statement.Replace(operation == "load" ? "LOAD " : "SERVICE ",
            operation == "load" ? "LOAD SILENT " : "SERVICE SILENT ", StringComparison.Ordinal);
        using var accepted = await client.PostAsync(path, new StringContent(silent, Encoding.UTF8, media));
        Assert.AreEqual(mode == "budget" ? HttpStatusCode.UnprocessableEntity : operation == "query" ? HttpStatusCode.OK : HttpStatusCode.NoContent, accepted.StatusCode,
            await accepted.Content.ReadAsStringAsync());
        if (operation == "query" && mode != "budget")
        {
            var result = await accepted.Content.ReadFromJsonAsync<JsonElement>();
            var bindings = result.GetProperty("results").GetProperty("bindings");
            Assert.AreEqual(1, bindings.GetArrayLength());
            Assert.AreEqual(0, bindings[0].EnumerateObject().Count());
        }
        using var final = await client.GetAsync("/sparql?query=" + Uri.EscapeDataString("ASK { <urn:seed> <urn:p> 1 }"));
        Assert.AreEqual(HttpStatusCode.OK, final.StatusCode);
        Assert.AreEqual(operation != "query" && mode != "budget", (await final.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("boolean").GetBoolean());
        Assert.AreEqual(mode == "denied" ? 0 : 2, fetched);
    }
}
