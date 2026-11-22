using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Sparql_json_term_objects_preserve_metadata_and_document_blank_identity()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string query = "SELECT ?iri ?plain ?lang ?typed ?a ?same ?other ?missing { " +
            "BIND(<urn:雪> AS ?iri) BIND('' AS ?plain) BIND('雪'@en AS ?lang) " +
            "BIND('007'^^<urn:custom> AS ?typed) BIND(BNODE() AS ?a) " +
            "BIND(?a AS ?same) BIND(BNODE() AS ?other) VALUES ?repeat { 1 2 } }";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sparql?query=" + Uri.EscapeDataString(query));
        request.Headers.Accept.ParseAdd("application/sparql-results+json");
        using var response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("application/sparql-results+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        CollectionAssert.AreEquivalent(new[] { "head", "results" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        var names = root.GetProperty("head").GetProperty("vars").EnumerateArray().Select(v => v.GetString()).ToArray();
        CollectionAssert.AreEqual(new[] { "iri", "plain", "lang", "typed", "a", "same", "other", "missing" }, names);
        var rows = root.GetProperty("results").GetProperty("bindings");
        Assert.AreEqual(2, rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            Assert.AreEqual(7, row.EnumerateObject().Count());
            Assert.IsFalse(row.TryGetProperty("missing", out _));
            Assert.IsTrue(row.EnumerateObject().All(p => names.Contains(p.Name)));
            AssertTerm(row.GetProperty("iri"), "uri", "urn:雪");
            AssertTerm(row.GetProperty("plain"), "literal", "", "datatype", "http://www.w3.org/2001/XMLSchema#string");
            AssertTerm(row.GetProperty("lang"), "literal", "雪", "xml:lang", "en");
            AssertTerm(row.GetProperty("typed"), "literal", "007", "datatype", "urn:custom");
            foreach (string name in new[] { "a", "same", "other" })
            {
                var term = row.GetProperty(name);
                AssertTerm(term, "bnode", term.GetProperty("value").GetString()!);
            }
            Assert.AreEqual(row.GetProperty("a").GetProperty("value").GetString(), row.GetProperty("same").GetProperty("value").GetString());
            Assert.AreNotEqual(row.GetProperty("a").GetProperty("value").GetString(), row.GetProperty("other").GetProperty("value").GetString());
        }
        Assert.AreEqual(rows[0].GetProperty("a").GetProperty("value").GetString(), rows[1].GetProperty("a").GetProperty("value").GetString());

        static void AssertTerm(JsonElement term, string type, string value, string? metadata = null, string? metadataValue = null)
        {
            Assert.AreEqual(type, term.GetProperty("type").GetString());
            Assert.AreEqual(value, term.GetProperty("value").GetString());
            Assert.AreEqual(metadata is null ? 2 : 3, term.EnumerateObject().Count());
            if (metadata is not null)
            {
                Assert.AreEqual(metadataValue, term.GetProperty(metadata).GetString());
            }
        }
    }

    [TestMethod]
    [DataRow("application/sparql-results+json")]
    [DataRow("application/sparql-results+xml")]
    [DataRow("text/csv")]
    [DataRow("text/tab-separated-values")]
    public async Task Sparql_result_formats_preserve_projection_order_and_empty_solution_multiplicity(string media)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string query = "SELECT ?z ?a { VALUES (?z ?a) { (UNDEF UNDEF) ('last' 1) (UNDEF UNDEF) ('first' 2) } } ORDER BY DESC(?a)";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sparql?query=" + Uri.EscapeDataString(query));
        request.Headers.Accept.ParseAdd(media);
        using var response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual(media, response.Content.Headers.ContentType?.MediaType);
        using var parsed = new SparqlResultSet();
        switch (media)
        {
            case "application/sparql-results+json":
                new SparqlJsonParser().Load(parsed, new StringReader(body));
                break;
            case "application/sparql-results+xml":
                new SparqlXmlParser().Load(parsed, new StringReader(body));
                break;
            case "text/csv":
                Assert.AreEqual("z,a\r\nfirst,2\r\nlast,1\r\n,\r\n,\r\n", body);
                new SparqlCsvParser().Load(parsed, new StringReader(body));
                break;
            default:
                Assert.IsTrue(body.StartsWith("?z\t?a\n", StringComparison.Ordinal), body);
                Assert.IsTrue(body.EndsWith("\n\t\n\t\n", StringComparison.Ordinal), body);
                new SparqlTsvParser().Load(parsed, new StringReader(body));
                break;
        }
        CollectionAssert.AreEqual(new[] { "z", "a" }, parsed.Variables.ToArray());
        Assert.AreEqual(4, parsed.Count, body);
        Assert.AreEqual("first", ((ILiteralNode)parsed[0]["z"]).Value);
        Assert.AreEqual("2", ((ILiteralNode)parsed[0]["a"]).Value);
        Assert.AreEqual("last", ((ILiteralNode)parsed[1]["z"]).Value);
        Assert.AreEqual("1", ((ILiteralNode)parsed[1]["a"]).Value);
        foreach (int index in new[] { 2, 3 })
        {
            Assert.IsFalse(parsed[index].HasBoundValue("z"));
            Assert.IsFalse(parsed[index].HasBoundValue("a"));
        }
    }

    [TestMethod]
    [DataRow("json")]
    [DataRow("xml")]
    public async Task Sparql_result_documents_distinguish_zero_rows_empty_mapping_and_false(string format)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (var (query, count) in new[] { ("SELECT * {}", 1), ("SELECT * { FILTER(false) }", 0), ("ASK { FILTER(false) }", -1) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/sparql?query=" + Uri.EscapeDataString(query));
            request.Headers.Accept.ParseAdd("application/sparql-results+" + format);
            using var response = await client.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
            if (format == "json")
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (count < 0)
                {
                    Assert.IsFalse(root.GetProperty("boolean").GetBoolean());
                    Assert.IsFalse(root.TryGetProperty("results", out _));
                }
                else
                {
                    Assert.AreEqual(0, root.GetProperty("head").GetProperty("vars").GetArrayLength());
                    var rows = root.GetProperty("results").GetProperty("bindings");
                    Assert.AreEqual(count, rows.GetArrayLength());
                    foreach (var row in rows.EnumerateArray())
                    {
                        Assert.AreEqual(0, row.EnumerateObject().Count());
                    }
                    Assert.IsFalse(root.TryGetProperty("boolean", out _));
                }
            }
            else
            {
                XNamespace ns = "http://www.w3.org/2005/sparql-results#";
                var root = XDocument.Parse(body).Root!;
                Assert.AreEqual(ns + "sparql", root.Name);
                Assert.AreEqual(0, root.Element(ns + "head")!.Elements(ns + "variable").Count());
                if (count < 0)
                {
                    Assert.AreEqual("false", root.Element(ns + "boolean")!.Value);
                    Assert.IsNull(root.Element(ns + "results"));
                }
                else
                {
                    var rows = root.Element(ns + "results")!.Elements(ns + "result").ToArray();
                    Assert.AreEqual(count, rows.Length);
                    foreach (var row in rows)
                    {
                        Assert.IsFalse(row.HasElements);
                    }
                    Assert.IsNull(root.Element(ns + "boolean"));
                }
            }
        }
    }
}
