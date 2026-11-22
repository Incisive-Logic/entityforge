using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Sparql11_graph_store_multipart_is_atomic_and_scopes_blank_nodes_per_part()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string path = "/sparql/graphs/nested/data.ttl";
        using (var payload = new MultipartFormDataContent())
        {
            payload.Add(new StringContent("_:a <urn:p> 1 .", Encoding.UTF8, "text/turtle"), "one", "one.ttl");
            payload.Add(new StringContent("_:a <urn:p> 2 .", Encoding.UTF8, "text/turtle"), "two", "two.ttl");
            using var response = await client.PostAsync(path, payload);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        using var stored = new MemoryStream(Encoding.UTF8.GetBytes(await client.GetStringAsync(path)));
        var quads = EntityForge.Db.Rdf.RdfSyntax.ParseTurtle(stored, "multipart");
        Assert.AreEqual(2, quads.Select(quad => quad.Subject).Distinct().Count());
        foreach (var (type, body) in new[] { ("text/plain", "unsupported"), ("text/turtle", "<urn:invalid>") })
        {
            using var payload = new MultipartFormDataContent();
            payload.Add(new StringContent("<urn:partial> <urn:p> 3 .", Encoding.UTF8, "text/turtle"), "first");
            payload.Add(new StringContent(body, Encoding.UTF8, type), "second");
            using var response = await client.PostAsync(path, payload);
            Assert.AreEqual(type == "text/plain" ? HttpStatusCode.UnsupportedMediaType : HttpStatusCode.BadRequest,
                response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.IsFalse((await client.GetStringAsync(path)).Contains("urn:partial", StringComparison.Ordinal));
        }
        using (var payload = new MultipartFormDataContent())
        {
            for (int index = 0; index < 17; index++)
            {
                payload.Add(new StringContent("<urn:partial> <urn:p> 3 .", Encoding.UTF8, "text/turtle"), "part" + index);
            }
            using var response = await client.PostAsync(path, payload);
            Assert.IsFalse(response.IsSuccessStatusCode);
            Assert.IsFalse((await client.GetStringAsync(path)).Contains("urn:partial", StringComparison.Ordinal));
        }
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("?default")]
    [DataRow("?graph=urn%3Atarget")]
    public async Task Sparql11_graph_store_replaces_merges_reads_and_deletes_one_graph(string selector)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string path = "/sparql/graphs" + selector;
        using var other = await client.PutAsync("/sparql/graphs?graph=urn%3Aother", new StringContent("<urn:other> <urn:p> 9 .", Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.Created, other.StatusCode);
        using var created = await client.PutAsync(path, new StringContent("<urn:one> <urn:p> 1 .", Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(selector == "?default" ? HttpStatusCode.NoContent : HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        using var merged = await client.PostAsync(path, new StringContent("<urn:two> <urn:p> 2 .", Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.NoContent, merged.StatusCode);
        string both = await client.GetStringAsync(path);
        StringAssert.Contains(both, "urn:one");
        StringAssert.Contains(both, "urn:two");
        foreach (string format in new[] { "text/turtle", "application/n-triples", "application/rdf+xml", "application/ld+json" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Accept.ParseAdd(format);
            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(format, response.Content.Headers.ContentType!.MediaType);
        }
        using var head = await client.SendAsync(new(HttpMethod.Head, path));
        Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
        Assert.AreEqual(0, (await head.Content.ReadAsByteArrayAsync()).Length);
        using var replaced = await client.PutAsync(path, new StringContent("<urn:three> <urn:p> 3 .", Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.NoContent, replaced.StatusCode);
        string replacement = await client.GetStringAsync(path);
        StringAssert.Contains(replacement, "urn:three");
        Assert.IsFalse(replacement.Contains("urn:one", StringComparison.Ordinal));
        Assert.IsFalse(replacement.Contains("urn:two", StringComparison.Ordinal));
        using var deleted = await client.DeleteAsync(path);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        using var absent = await client.GetAsync(path);
        Assert.AreEqual(selector == "?default" ? HttpStatusCode.OK : HttpStatusCode.NotFound, absent.StatusCode);
        StringAssert.Contains(await client.GetStringAsync("/sparql/graphs?graph=urn%3Aother"), "urn:other");
    }

    [TestMethod]
    public async Task Sparql11_graph_store_generated_address_and_blank_nodes_survive_restart()
    {
        _directory = NewDirectory();
        string? path = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            if (attempt == 0)
            {
                using var created = await client.PostAsync("/sparql/graphs", new StringContent("_:a <urn:p> 1 .", Encoding.UTF8, "text/turtle"));
                Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
                var address = created.Headers.Location!;
                // Retain the graph IRI even when the test host restarts on a new port.
                path = "/sparql/graphs?graph=" + Uri.EscapeDataString(address.AbsoluteUri);
                using var direct = await client.GetAsync(address);
                Assert.AreEqual(HttpStatusCode.OK, direct.StatusCode);
                using var appended = await client.PostAsync(path, new StringContent("_:a <urn:p> 1 .", Encoding.UTF8, "text/turtle"));
                Assert.AreEqual(HttpStatusCode.NoContent, appended.StatusCode);
            }
            string text = await client.GetStringAsync(path);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            Assert.AreEqual(2, EntityForge.Db.Rdf.RdfSyntax.ParseTurtle(stream, "check").Count);
            await host.StopAsync();
        }
    }

    [TestMethod]
    [DataRow("?graph=relative", "text/turtle", "<urn:a> <urn:p> 1 .", 400)]
    [DataRow("?graph=urn:g&default", "text/turtle", "<urn:a> <urn:p> 1 .", 400)]
    [DataRow("?default=true", "text/turtle", "<urn:a> <urn:p> 1 .", 400)]
    [DataRow("?graph=urn:g&graph=urn:h", "text/turtle", "<urn:a> <urn:p> 1 .", 400)]
    [DataRow("?graph=urn:g", "application/json", "{}", 415)]
    [DataRow("?graph=urn:g", "text/turtle", "<urn:bad> <urn:p> 2 . invalid", 400)]
    [DataRow("?graph=urn:g", "application/rdf+xml", "<!DOCTYPE rdf [<!ENTITY x SYSTEM 'file:///nonexistent'>]><rdf/>", 400)]
    public async Task Sparql11_graph_store_rejects_invalid_input_without_clearing_existing_data(string selector, string media, string payload, int status)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PutAsync("/sparql/graphs?graph=urn:g", new StringContent("<urn:kept> <urn:p> 1 .", Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.Created, seed.StatusCode);
        using var response = await client.PutAsync("/sparql/graphs" + selector, new StringContent(payload, Encoding.UTF8, media));
        Assert.AreEqual((HttpStatusCode)status, response.StatusCode, await response.Content.ReadAsStringAsync());
        StringAssert.Contains(await client.GetStringAsync("/sparql/graphs?graph=urn:g"), "urn:kept");
    }

    [TestMethod]
    public async Task Sparql11_graph_store_requires_write_access_but_allows_read_only_HEAD()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var reader = Client(host, ReaderToken);
        using var writer = Client(host, WriterToken);
        using var anonymous = Client(host);
        using var hidden = await anonymous.GetAsync("/sparql/graphs?default");
        Assert.AreEqual(HttpStatusCode.Unauthorized, hidden.StatusCode);
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Post, HttpMethod.Delete })
        {
            using var rejected = await reader.SendAsync(new(method, "/sparql/graphs?default"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        using var head = await reader.SendAsync(new(HttpMethod.Head, "/sparql/graphs?default"));
        Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
        using var allowed = await writer.PutAsync("/sparql/graphs?default", new StringContent("<urn:a> <urn:p> 1 .", Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.NoContent, allowed.StatusCode);
    }

    [TestMethod]
    [DataRow("application/n-triples", "<urn:a> <urn:p> \"text\" .")]
    [DataRow("application/rdf+xml", "<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#' xmlns:x='urn:'><rdf:Description rdf:about='urn:a'><x:p>text</x:p></rdf:Description></rdf:RDF>")]
    public async Task Sparql11_graph_store_accepts_standard_RDF_representations(string media, string payload)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PutAsync("/sparql/graphs?graph=urn:g", new StringContent(payload, Encoding.UTF8, media));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        StringAssert.Contains(await client.GetStringAsync("/sparql/graphs?graph=urn:g"), "\"text\"");
    }

    [TestMethod]
    public async Task Sparql11_graph_store_bounds_fail_before_mutating_the_graph()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string path = "/sparql/graphs?graph=urn:g";
        using var seed = await client.PutAsync(path, new StringContent("<urn:kept> <urn:p> 1 .", Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.Created, seed.StatusCode);
        foreach (string body in new[]
        {
            String.Join('\n', Enumerable.Range(0, 4097).Select(index => $"<urn:item{index}> <urn:p> 1 .")),
            "<urn:x> <urn:p> " + new string('[', 65) + new string(']', 65) + " .",
            new string(' ', 4 * 1024 * 1024 + 1)
        })
        {
            using var response = await client.PutAsync(path, new StringContent(body, Encoding.UTF8, "text/turtle"));
            Assert.IsFalse(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            StringAssert.Contains(await client.GetStringAsync(path), "urn:kept");
        }
    }

    [TestMethod]
    public async Task Sparql11_graph_store_concurrent_replacements_never_merge_their_payloads()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => client.PutAsync("/sparql/graphs?graph=urn:g",
            new StringContent($"<urn:item{index}> <urn:p> {index} .", Encoding.UTF8, "text/turtle"))));
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.IsTrue(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            }
        }
        Assert.AreEqual(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        using var result = await client.PostAsync("/sparql", new StringContent("SELECT (COUNT(*) AS ?count) WHERE { GRAPH <urn:g> {?s ?p ?o} }", Encoding.UTF8, "application/sparql-query"));
        var json = await result.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("1", json.GetProperty("results").GetProperty("bindings")[0].GetProperty("count").GetProperty("value").GetString());
    }
}
