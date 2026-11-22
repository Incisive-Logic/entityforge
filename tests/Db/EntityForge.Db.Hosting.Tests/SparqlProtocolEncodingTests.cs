using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("GET")]
    [DataRow("FORM")]
    [DataRow("UPDATE")]
    public async Task Sparql11_protocol_rejects_invalid_percent_encoded_utf8_without_changes(string transport)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        var invalidEncodings = new[] { "%FF", "%C0%AF", "%ED%A0%80", "%E2%82", "%F4%90%80%80" }
            .Concat(transport == "GET" ? [] : new[] { "%", "%0", "%GG" });
        foreach (string invalid in invalidEncodings)
        {
            string statement = transport == "UPDATE" ? "INSERT DATA { <urn:a> <urn:p> 'ENCODED' }" : "SELECT ('ENCODED' AS ?value) {}";
            string fields = (transport == "UPDATE" ? "update=" : "query=") +
                Uri.EscapeDataString(statement).Replace("ENCODED", invalid, StringComparison.Ordinal);
            using var request = new HttpRequestMessage(transport == "GET" ? HttpMethod.Get : HttpMethod.Post,
                transport == "GET" ? "/sparql?" + fields : transport == "UPDATE" ? "/sparql/update" : "/sparql");
            if (transport != "GET")
            {
                request.Content = new StringContent(fields, Encoding.UTF8, "application/x-www-form-urlencoded");
            }
            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, invalid + ": " + await response.Content.ReadAsStringAsync());
        }
        using var ask = await client.GetAsync("/sparql?query=" + Uri.EscapeDataString("ASK { ?s ?p ?o }"));
        Assert.AreEqual(HttpStatusCode.OK, ask.StatusCode);
        Assert.IsFalse((await ask.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("boolean").GetBoolean());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql11_protocol_rejects_invalid_encoded_dataset_selectors(bool update)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsync(update ? "/sparql/update?using-graph-uri=urn:%FF" : "/sparql?default-graph-uri=urn:%FF",
            new StringContent(update ? "INSERT DATA { <urn:a> <urn:p> 1 }" : "ASK {}", Encoding.UTF8,
                update ? "application/sparql-update" : "application/sparql-query"));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var ask = await client.GetAsync("/sparql?query=" + Uri.EscapeDataString("ASK { ?s ?p ?o }"));
        Assert.AreEqual(HttpStatusCode.OK, ask.StatusCode);
        Assert.IsFalse((await ask.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("boolean").GetBoolean());
    }

    [TestMethod]
    [DataRow("GET")]
    [DataRow("FORM")]
    [DataRow("RAW")]
    public async Task Sparql11_protocol_preserves_unicode_and_literal_percent_sequences(string transport)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string expected = "caf\u00e9 \u20ac \U0001F600 + %FF & =";
        string query = "SELECT ('" + expected + "' AS ?value) {}";
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
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(expected, result.GetProperty("results").GetProperty("bindings")[0].GetProperty("value").GetProperty("value").GetString());
    }
}
