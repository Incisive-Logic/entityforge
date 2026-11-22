using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("application/json", "{}", "", 415)]
    [DataRow("application/x-www-form-urlencoded", "query=ASK%7B%7D", "", 400)]
    [DataRow("application/x-www-form-urlencoded", "update=CLEAR+ALL&update=CLEAR+ALL", "", 400)]
    [DataRow("application/x-www-form-urlencoded", "update=CLEAR+ALL", "?using-graph-uri=urn:g", 400)]
    [DataRow("application/sparql-update", "CLEAR ALL", "?update=CLEAR+ALL", 400)]
    [DataRow("application/sparql-update", "CLEAR ALL", "?using-graph-uri=relative", 400)]
    [DataRow("application/sparql-update", "CLEAR ALL", "?using-graph-uri=urn%3Ax%3E", 400)]
    [DataRow("application/sparql-update", ";", "", 400)]
    public async Task Sparql11_protocol_update_rejects_invalid_transport(string media, string body, string suffix, int status)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsync("/sparql/update" + suffix, new StringContent(body, Encoding.UTF8, media));
        Assert.AreEqual((HttpStatusCode)status, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql11_protocol_update_uses_write_authorization_and_atomic_native_updates(bool form)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var writer = Client(host, WriterToken);
        using var reader = Client(host, ReaderToken);
        using var anonymous = Client(host);
        async Task<HttpResponseMessage> Submit(HttpClient client, string update, params (string Key, string Value)[] dataset)
        {
            string path = "/sparql/update";
            HttpContent content;
            if (form)
            {
                content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("update", update) }
                    .Concat(dataset.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value))));
            }
            else
            {
                content = new StringContent(update, Encoding.UTF8, "application/sparql-update");
                if (dataset.Length > 0)
                {
                    path += "?" + String.Join("&", dataset.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));
                }
            }
            using (content)
            {
                return await client.PostAsync(path, content);
            }
        }
        const string seed = "INSERT DATA { <urn:default> <urn:p> 1 . GRAPH <urn:g> { <urn:a> <urn:p> 2 } GRAPH <urn:h> { <urn:b> <urn:p> 3 } }";
        using var unauthorized = await Submit(anonymous, seed);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var forbidden = await Submit(reader, seed);
        Assert.AreEqual(HttpStatusCode.Unauthorized, forbidden.StatusCode);
        using var empty = await Submit(writer, "");
        Assert.AreEqual(HttpStatusCode.NoContent, empty.StatusCode);
        using var seeded = await Submit(writer, seed);
        Assert.AreEqual(HttpStatusCode.NoContent, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
        using var copied = await Submit(writer,
            "INSERT { ?s <urn:copy> ?o } WHERE { ?s <urn:p> ?o }; INSERT { ?s <urn:seen> ?o } WHERE { ?s <urn:p> ?o }",
            ("using-graph-uri", "urn:g"), ("using-graph-uri", "urn:h"));
        Assert.AreEqual(HttpStatusCode.NoContent, copied.StatusCode, await copied.Content.ReadAsStringAsync());
        using var named = await Submit(writer,
            "INSERT { ?s <urn:named> ?o } WHERE { GRAPH ?g { ?s <urn:p> ?o } }", ("using-named-graph-uri", "urn:g"));
        Assert.AreEqual(HttpStatusCode.NoContent, named.StatusCode, await named.Content.ReadAsStringAsync());
        using var emptyDefault = await Submit(writer,
            "INSERT { ?s <urn:unexpected> ?o } WHERE { ?s <urn:p> ?o }", ("using-named-graph-uri", "urn:g"));
        Assert.AreEqual(HttpStatusCode.NoContent, emptyDefault.StatusCode, await emptyDefault.Content.ReadAsStringAsync());
        using var keyword = await Submit(writer,
            "PREFIX USING: <urn:> INSERT { USING:literal USING:text 'WITH USING WHERE' } WHERE {}", ("using-graph-uri", "urn:g"));
        Assert.AreEqual(HttpStatusCode.NoContent, keyword.StatusCode, await keyword.Content.ReadAsStringAsync());
        using var removed = await Submit(writer, "DELETE WHERE { ?s <urn:p> ?o }", ("using-graph-uri", "urn:g"));
        Assert.AreEqual(HttpStatusCode.NoContent, removed.StatusCode, await removed.Content.ReadAsStringAsync());
        foreach (string invalid in new[]
        {
            "WITH <urn:g> INSERT { ?s <urn:bad> ?o } WHERE { ?s <urn:p> ?o }",
            "INSERT { ?s <urn:bad> ?o } USING <urn:g> WHERE { ?s <urn:p> ?o }",
            "DELETE WHERE { ?s <urn:p> ?o FILTER(true) }",
            "INSERT DATA { <urn:bad> <urn:p> 9 }; INSERT { ?s <urn:bad> ?o } USING <urn:g> WHERE { ?s <urn:p> ?o }"
        })
        {
            using var failed = await Submit(writer, invalid, ("using-graph-uri", "urn:g"));
            Assert.AreEqual(HttpStatusCode.BadRequest, failed.StatusCode, await failed.Content.ReadAsStringAsync());
        }
        using var query = await reader.PostAsJsonAsync("/v1/rdf/sparql", new
        {
            query = "ASK { <urn:default> <urn:p> 1 . <urn:a> <urn:copy> 2; <urn:seen> 2; <urn:named> 2 . <urn:b> <urn:copy> 3; <urn:seen> 3 . <urn:literal> <urn:text> 'WITH USING WHERE' . GRAPH <urn:g> { <urn:a> <urn:p> 2 } FILTER NOT EXISTS { <urn:bad> ?p ?o } FILTER NOT EXISTS { <urn:default> <urn:copy> ?o } FILTER NOT EXISTS { <urn:b> <urn:named> ?o } FILTER NOT EXISTS { ?s <urn:unexpected> ?o } }"
        });
        Assert.AreEqual(HttpStatusCode.OK, query.StatusCode, await query.Content.ReadAsStringAsync());
        Assert.IsTrue((await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("value").GetBoolean());
    }
}
