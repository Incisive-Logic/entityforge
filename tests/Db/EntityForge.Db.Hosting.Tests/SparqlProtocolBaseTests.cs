using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("dir/")]
    [DataRow("../other/")]
    [DataRow("")]
    [DataRow("//other.test/base/")]
    public async Task Sparql11_protocol_rejects_relative_BASE_declarations_without_mutations(string relativeBase)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string prolog = "BASE <https://example.test/base/> BASE <" + relativeBase + "> ";
        string query = prolog + "ASK {}";
        foreach (string transport in new[] { "GET", "FORM", "RAW" })
        {
            using var request = new HttpRequestMessage(transport == "GET" ? HttpMethod.Get : HttpMethod.Post,
                transport == "GET" ? "/sparql?query=" + Uri.EscapeDataString(query) : "/sparql");
            if (transport == "FORM")
            {
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["query"] = query });
            }
            else if (transport == "RAW")
            {
                request.Content = new StringContent(query, Encoding.UTF8, "application/sparql-query");
            }
            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        using var update = await client.PostAsync("/sparql/update", new StringContent(
            "INSERT DATA { <urn:should-not-exist> <urn:p> 1 }; " + prolog + "INSERT DATA { <s> <p> 2 }",
            Encoding.UTF8, "application/sparql-update"));
        Assert.AreEqual(HttpStatusCode.BadRequest, update.StatusCode, await update.Content.ReadAsStringAsync());
        using var ask = await client.GetAsync("/sparql?query=" + Uri.EscapeDataString("ASK { ?s ?p ?o }"));
        Assert.AreEqual(HttpStatusCode.OK, ask.StatusCode);
        Assert.IsFalse((await ask.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("boolean").GetBoolean());
    }

    [TestMethod]
    [DataRow("INSERT { ?s <urn:copy> ?o } WHERE { ?s <urn:p> ?o }")]
    [DataRow("INSERT { ?s <urn:copy> ?o } WHERE { ?s <urn:p> ?o }; INSERT { ?s <urn:seen> ?o } WHERE { ?s <urn:p> ?o }")]
    public void Sparql11_protocol_dataset_updates_preserve_base_prologs(string statement)
    {
        string rewritten = EntityForge.Db.Rdf.RdfSparqlUpdate.WithProtocolDataset("BASE <http://example.org/sparql>\n" + statement, ["urn:g", "urn:h"], []);
        try
        {
            Assert.AreEqual(0, EntityForge.Db.Rdf.RdfSparqlUpdate.ParseWhere([], rewritten, "protocol-base", default).Count);
        }
        catch (Exception exception)
        {
            Assert.Fail(rewritten + "\n" + exception);
        }
    }

    [TestMethod]
    [DataRow("GET", "", "thing")]
    [DataRow("FORM", "", "thing")]
    [DataRow("RAW", "", "thing")]
    [DataRow("RAW", "BASE <https://example.org/dir/>", "https://example.org/dir/thing")]
    [DataRow("RAW", "BASE <https://example.org/base/>", "https://example.org/base/thing")]
    public async Task Sparql11_protocol_defines_a_service_base_and_honors_explicit_bases(string transport, string prolog, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string query = prolog + " SELECT (<thing> AS ?value) {}";
        using var request = new HttpRequestMessage(transport == "GET" ? HttpMethod.Get : HttpMethod.Post,
            transport == "GET" ? "/sparql?query=" + Uri.EscapeDataString(query) : "/sparql");
        if (transport == "RAW")
        {
            request.Content = new StringContent(query, Encoding.UTF8, "application/sparql-query");
        }
        else if (transport == "FORM")
        {
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["query"] = query });
        }
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(new Uri(client.BaseAddress!, expected).AbsoluteUri,
            result.GetProperty("results").GetProperty("bindings")[0].GetProperty("value").GetProperty("value").GetString());
        using var update = await client.PostAsync("/sparql/update", new StringContent(
            "INSERT DATA { <thing> <property> 'value' }", Encoding.UTF8, "application/sparql-update"));
        Assert.AreEqual(HttpStatusCode.NoContent, update.StatusCode, await update.Content.ReadAsStringAsync());
        using var ask = await client.GetAsync("/sparql?query=" + Uri.EscapeDataString("ASK { <thing> <property> 'value' }"));
        Assert.AreEqual(HttpStatusCode.OK, ask.StatusCode);
        Assert.IsTrue((await ask.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("boolean").GetBoolean());
    }

    [TestMethod]
    [DataRow("/sparql", "application/sparql-query", "ASK {}")]
    [DataRow("/sparql/update", "application/sparql-update", "INSERT DATA { <urn:a> <urn:p> 1 }")]
    public async Task Sparql11_protocol_rejects_non_utf8_charsets_and_get_bodies(string path, string mediaType, string body)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsync(path, new StringContent(body, Encoding.Unicode, mediaType));
        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sparql?query=ASK%7B%7D")
        {
            Content = new StringContent("not permitted")
        };
        using var get = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadRequest, get.StatusCode, await get.Content.ReadAsStringAsync());
    }
}
