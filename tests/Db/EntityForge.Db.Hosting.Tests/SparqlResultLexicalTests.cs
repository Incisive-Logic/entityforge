using System.Net;
using Microsoft.AspNetCore.Hosting;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("json", "a\\rb\\nc\\td", "a\rb\nc\td")]
    [DataRow("xml", "a\\rb\\nc\\td", "a\rb\nc\td")]
    [DataRow("json", "a\\r\\nb", "a\r\nb")]
    [DataRow("xml", "a\\r\\nb", "a\r\nb")]
    [DataRow("json", "<&😀\\u0085\\u2028", "<&😀\u0085\u2028")]
    [DataRow("xml", "<&😀\\u0085\\u2028", "<&😀\u0085\u2028")]
    [DataRow("json", "\\u0001", "\u0001")]
    public async Task Sparql_result_formats_preserve_literal_lexical_values(string format, string escaped, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string query = $"SELECT ?plain ?language ?typed WHERE {{ BIND('{escaped}' AS ?plain) BIND('{escaped}'@en AS ?language) BIND('{escaped}'^^<urn:custom> AS ?typed) }}";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sparql?query=" + Uri.EscapeDataString(query));
        request.Headers.Accept.ParseAdd("application/sparql-results+" + format);
        using var response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        var parsed = new SparqlResultSet();
        if (format == "xml")
        {
            new SparqlXmlParser().Load(parsed, new StringReader(body));
        }
        else
        {
            new SparqlJsonParser().Load(parsed, new StringReader(body));
        }
        Assert.AreEqual(1, parsed.Count);
        foreach (string variable in new[] { "plain", "language", "typed" })
        {
            Assert.AreEqual(expected, ((ILiteralNode)parsed[0][variable]).Value, variable);
        }
        Assert.AreEqual("en", ((ILiteralNode)parsed[0]["language"]).Language);
        Assert.AreEqual("urn:custom", ((ILiteralNode)parsed[0]["typed"]).DataType.OriginalString);
    }

    [TestMethod]
    public async Task Sparql_xml_rejects_unrepresentable_characters_without_a_partial_result()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sparql?query=" + Uri.EscapeDataString("SELECT ?x { VALUES ?x { 'valid' '\\u0001' } }"));
        request.Headers.Accept.ParseAdd("application/sparql-results+xml");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
