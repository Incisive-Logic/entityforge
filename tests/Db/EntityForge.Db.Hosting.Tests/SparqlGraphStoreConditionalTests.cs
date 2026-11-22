using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Sparql11_graph_store_conditions_distinguish_absent_graph_collection_and_header_precedence()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string path = "/sparql/graphs/absent";
        foreach (string method in new[] { "GET", "DELETE", "PUT", "POST" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            request.Headers.TryAddWithoutValidation("If-Match", "*");
            if (method is "PUT" or "POST")
            {
                request.Content = new StringContent("<urn:absent> <urn:p> 1 .", Encoding.UTF8, "text/turtle");
            }
            using var response = await client.SendAsync(request);
            Assert.AreEqual(method is "GET" or "DELETE" ? HttpStatusCode.NotFound : HttpStatusCode.PreconditionFailed,
                response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        using (var generated = new HttpRequestMessage(HttpMethod.Post, "/sparql/graphs")
        {
            Content = new StringContent("<urn:unexpected> <urn:p> 1 .", Encoding.UTF8, "text/turtle")
        })
        {
            generated.Headers.TryAddWithoutValidation("If-None-Match", "*");
            using var rejected = await client.SendAsync(generated);
            Assert.AreEqual(HttpStatusCode.PreconditionFailed, rejected.StatusCode);
            Assert.IsNull(rejected.Headers.Location);
        }
        using (var check = JsonDocument.Parse(await client.GetStringAsync("/sparql?query=" + Uri.EscapeDataString("ASK { GRAPH ?g { ?s ?p ?o } }"))))
        {
            Assert.IsFalse(check.RootElement.GetProperty("boolean").GetBoolean());
        }
        using var seed = await client.PutAsync(path, new StringContent("<urn:kept> <urn:p> 1 .", Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.Created, seed.StatusCode);
        using (var precedence = new HttpRequestMessage(HttpMethod.Get, path))
        {
            precedence.Headers.TryAddWithoutValidation("If-Match", "\"unknown\"");
            precedence.Headers.TryAddWithoutValidation("If-None-Match", "*");
            using var rejected = await client.SendAsync(precedence);
            Assert.AreEqual(HttpStatusCode.PreconditionFailed, rejected.StatusCode);
        }
        using var permitted = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent("<urn:updated> <urn:p> 2 .", Encoding.UTF8, "text/turtle")
        };
        permitted.Headers.TryAddWithoutValidation("If-Match", "*");
        using var updated = await client.SendAsync(permitted);
        Assert.AreEqual(HttpStatusCode.NoContent, updated.StatusCode);
        StringAssert.Contains(await client.GetStringAsync(path), "urn:updated");
    }

    [TestMethod]
    [DataRow("PUT")]
    [DataRow("POST")]
    [DataRow("DELETE")]
    public async Task Sparql11_graph_store_failed_preconditions_preserve_existing_graph(string method)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string path = "/sparql/graphs/conditional";
        using var seed = await client.PutAsync(path, new StringContent("<urn:original> <urn:p> 1 .", Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.Created, seed.StatusCode);
        string before = await client.GetStringAsync(path);
        foreach (var (header, value) in new[] { ("If-None-Match", "*"), ("If-Match", "\"unissued-tag\""), ("If-Match", "W/\"unissued-tag\"") })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            request.Headers.TryAddWithoutValidation(header, value);
            if (method != "DELETE")
            {
                request.Content = new StringContent("<urn:replacement> <urn:p> 2 .", Encoding.UTF8, "text/turtle");
            }
            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.PreconditionFailed, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(before, await client.GetStringAsync(path));
        }
    }

    [TestMethod]
    [DataRow("GET", "If-None-Match", "*", 304)]
    [DataRow("HEAD", "If-None-Match", "*", 304)]
    [DataRow("GET", "If-Match", "*", 200)]
    [DataRow("HEAD", "If-Match", "*", 200)]
    [DataRow("GET", "If-Match", "\"unissued-tag\"", 412)]
    [DataRow("GET", "If-None-Match", "\"unissued-tag\"", 200)]
    [DataRow("GET", "If-Match", "*, \"tag\"", 400)]
    public async Task Sparql11_graph_store_conditional_reads_handle_default_empty_graph(string method, string header, string value, int expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var request = new HttpRequestMessage(new HttpMethod(method), "/sparql/graphs?default");
        request.Headers.TryAddWithoutValidation(header, value);
        using var response = await client.SendAsync(request);
        Assert.AreEqual(expected, (int)response.StatusCode, await response.Content.ReadAsStringAsync());
        if (expected == 304 || method == "HEAD")
        {
            Assert.AreEqual(0, (await response.Content.ReadAsByteArrayAsync()).Length);
        }
    }

    [TestMethod]
    [DataRow("PUT")]
    [DataRow("POST")]
    public async Task Sparql11_graph_store_create_only_is_atomic_between_concurrent_writers(string method)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string path = "/sparql/graphs/create-only";
        async Task<(int Index, HttpStatusCode Status)> Write(int index)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = new StringContent($"<urn:writer:{index}> <urn:p> {index} .", Encoding.UTF8, "text/turtle")
            };
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            using var response = await client.SendAsync(request);
            return (index, response.StatusCode);
        }
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(Write));
        Assert.AreEqual(1, responses.Count(item => item.Status == HttpStatusCode.Created));
        Assert.AreEqual(7, responses.Count(item => item.Status == HttpStatusCode.PreconditionFailed));
        string stored = await client.GetStringAsync(path);
        foreach (var result in responses)
        {
            Assert.AreEqual(result.Status == HttpStatusCode.Created, stored.Contains($"urn:writer:{result.Index}", StringComparison.Ordinal));
        }
    }
}
