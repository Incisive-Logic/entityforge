using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("application/sparql-results+json")]
    [DataRow("application/sparql-results+xml")]
    [DataRow("text/csv")]
    [DataRow("text/tab-separated-values")]
    public async Task Sparql_protocol_subquery_values_headers_preserve_unbound_variables(string mediaType)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string query = "SELECT * { { SELECT * {} VALUES (?x ?y) { (1 UNDEF) } } }";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/sparql")
        {
            Content = new StringContent(query, Encoding.UTF8, "application/sparql-query")
        };
        request.Headers.Accept.ParseAdd(mediaType);
        using var response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual(mediaType, response.Content.Headers.ContentType?.MediaType);
        if (mediaType.EndsWith("+json", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(body);
            CollectionAssert.AreEqual(new[] { "x", "y" }, document.RootElement.GetProperty("head")
                .GetProperty("vars").EnumerateArray().Select(value => value.GetString()).ToArray());
            var rows = document.RootElement.GetProperty("results").GetProperty("bindings");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.AreEqual("1", rows[0].GetProperty("x").GetProperty("value").GetString());
            Assert.IsFalse(rows[0].TryGetProperty("y", out _));
        }
        else if (mediaType.EndsWith("+xml", StringComparison.Ordinal))
        {
            var document = XDocument.Parse(body);
            XNamespace ns = "http://www.w3.org/2005/sparql-results#";
            CollectionAssert.AreEqual(new[] { "x", "y" }, document.Descendants(ns + "variable")
                .Select(variable => (string?)variable.Attribute("name")).ToArray());
            var row = document.Descendants(ns + "result").Single();
            var binding = row.Elements(ns + "binding").Single();
            Assert.AreEqual("x", (string?)binding.Attribute("name"));
            Assert.AreEqual("1", binding.Element(ns + "literal")!.Value);
        }
        else
        {
            string normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
            Assert.AreEqual(mediaType == "text/csv" ? "x,y\n1," : "?x\t?y\n1\t", normalized);
        }
        await host.StopAsync();
    }
}
