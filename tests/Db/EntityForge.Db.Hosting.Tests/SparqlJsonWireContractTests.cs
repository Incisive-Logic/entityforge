using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("GET")]
    [DataRow("FORM")]
    [DataRow("RAW")]
    public async Task Sparql_json_wire_terms_and_bindings_obey_the_results_contract(string transport)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string query = """
            PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>
            SELECT ?iri ?text ?language ?typed ?empty ?blank ?same ?different ?unbound {
              BIND(<urn:contract:雪> AS ?iri)
              BIND('quote" slash\\ newline\n\t雪' AS ?text)
              BIND('bonjour'@fr AS ?language)
              BIND('001'^^xsd:integer AS ?typed)
              BIND('' AS ?empty)
              BIND(BNODE('shared') AS ?blank)
              BIND(?blank AS ?same)
              BIND(BNODE('other') AS ?different)
              BIND(7 AS ?hidden)
            }
            """;
        using var request = new HttpRequestMessage(transport == "GET" ? HttpMethod.Get : HttpMethod.Post,
            transport == "GET" ? "/sparql?query=" + Uri.EscapeDataString(query) : "/sparql");
        request.Headers.Accept.ParseAdd("application/sparql-results+json");
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
        Assert.AreEqual("application/sparql-results+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.AreEqual(JsonValueKind.Object, root.ValueKind);
        Assert.IsFalse(root.TryGetProperty("boolean", out _));
        string[] variables = root.GetProperty("head").GetProperty("vars").EnumerateArray()
            .Select(value => value.GetString()!).ToArray();
        CollectionAssert.AreEqual(new[] { "iri", "text", "language", "typed", "empty", "blank", "same", "different", "unbound" }, variables);
        var rows = root.GetProperty("results").GetProperty("bindings");
        Assert.AreEqual(1, rows.GetArrayLength());
        var row = rows[0];
        foreach (var binding in row.EnumerateObject())
        {
            CollectionAssert.Contains(variables, binding.Name, "Every binding must be declared in the result header.");
        }
        Assert.IsFalse(row.TryGetProperty("unbound", out _));
        Assert.IsFalse(row.TryGetProperty("hidden", out _));
        CheckTerm("iri", "uri", "urn:contract:雪");
        CheckTerm("text", "literal", "quote\" slash\\ newline\n\t雪");
        CheckTerm("language", "literal", "bonjour");
        CheckTerm("typed", "literal", "001");
        CheckTerm("empty", "literal", "");
        Assert.AreEqual("fr", row.GetProperty("language").GetProperty("xml:lang").GetString());
        Assert.IsFalse(row.GetProperty("language").TryGetProperty("datatype", out _));
        Assert.AreEqual("http://www.w3.org/2001/XMLSchema#integer", row.GetProperty("typed").GetProperty("datatype").GetString());
        Assert.IsFalse(row.GetProperty("typed").TryGetProperty("xml:lang", out _));
        foreach (string name in new[] { "blank", "same", "different" })
        {
            Assert.AreEqual("bnode", row.GetProperty(name).GetProperty("type").GetString());
            Assert.AreEqual(JsonValueKind.String, row.GetProperty(name).GetProperty("value").ValueKind);
        }
        Assert.AreEqual(row.GetProperty("blank").GetProperty("value").GetString(), row.GetProperty("same").GetProperty("value").GetString());
        Assert.AreNotEqual(row.GetProperty("blank").GetProperty("value").GetString(), row.GetProperty("different").GetProperty("value").GetString());
        // Labels establish identity within this document only. No comparison with
        // labels returned by another response is an identity guarantee.
        void CheckTerm(string name, string kind, string value)
        {
            var term = row.GetProperty(name);
            Assert.AreEqual(kind, term.GetProperty("type").GetString());
            Assert.AreEqual(JsonValueKind.String, term.GetProperty("value").ValueKind);
            Assert.AreEqual(value, term.GetProperty("value").GetString());
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_json_ask_has_a_boolean_value_and_head_object(bool expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string query = expected ? "ASK {}" : "ASK { FILTER(false) }";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sparql?query=" + Uri.EscapeDataString(query));
        request.Headers.Accept.ParseAdd("application/sparql-results+json");
        using var response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.AreEqual(JsonValueKind.Object, root.GetProperty("head").ValueKind);
        Assert.AreEqual(expected, root.GetProperty("boolean").GetBoolean());
        Assert.IsFalse(root.TryGetProperty("results", out _));
    }
}
