using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("GET")]
    [DataRow("HEAD")]
    [DataRow("PUT")]
    [DataRow("POST")]
    [DataRow("DELETE")]
    public async Task Sparql_graph_store_rejects_malformed_selector_encoding_before_access(string method)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        // Preserve a valid percent-encoded IRI while rejecting malformed transport encoding.
        string validPath = "/sparql/graphs?graph=" + Uri.EscapeDataString("urn:encoded:%EF%BF%BD");
        using var seed = await client.PutAsync(validPath, new StringContent("<urn:kept> <urn:p> 1 .", Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.Created, seed.StatusCode);
        string before = await client.GetStringAsync(validPath);
        foreach (string suffix in new[] { "%FF", "%C0%AF", "%ED%A0%80", "%E2%82", "%EF%BF%BD" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), "/sparql/graphs?graph=urn%3Aencoded%3A" + suffix);
            if (method is "PUT" or "POST")
            {
                request.Content = new StringContent("<urn:unexpected> <urn:p> 2 .", Encoding.UTF8, "text/turtle");
            }
            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, method + " " + suffix);
            Assert.AreEqual(before, await client.GetStringAsync(validPath), "Malformed encoding changed a valid graph.");
            using var unexpected = JsonDocument.Parse(await client.GetStringAsync("/sparql?query=" +
                Uri.EscapeDataString("ASK { GRAPH ?g { <urn:unexpected> ?p ?o } }")));
            Assert.IsFalse(unexpected.RootElement.GetProperty("boolean").GetBoolean(), "Rejected write created a different graph.");
        }
    }

    [TestMethod]
    public async Task Sparql_graph_store_preserves_encoded_unicode_and_literal_percent_graph_names()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (string graph in new[] { "urn:encoded:雪😀", "urn:encoded:%FF", "urn:encoded:+&=" })
        {
            string path = "/sparql/graphs?graph=" + Uri.EscapeDataString(graph);
            using var write = await client.PutAsync(path, new StringContent("<urn:kept> <urn:p> 1 .", Encoding.UTF8, "text/turtle"));
            Assert.AreEqual(HttpStatusCode.Created, write.StatusCode, graph);
            StringAssert.Contains(await client.GetStringAsync(path), "urn:kept");
            using var delete = await client.DeleteAsync(path);
            Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode);
            using var absent = await client.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.NotFound, absent.StatusCode);
        }
    }
}
