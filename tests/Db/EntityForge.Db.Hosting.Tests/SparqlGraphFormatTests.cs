using System.Net;
using System.Net.Http.Headers;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("text/turtle", false)]
    [DataRow("text/turtle", true)]
    [DataRow("application/n-triples", false)]
    [DataRow("application/n-triples", true)]
    [DataRow("application/ld+json", false)]
    [DataRow("application/ld+json", true)]
    [DataRow("application/rdf+xml", false)]
    [DataRow("application/rdf+xml", true)]
    public async Task Sparql_graph_formats_preserve_terms_and_blank_node_links(string format, bool empty)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string query = empty ? "CONSTRUCT { ?s <urn:p> ?o } WHERE { ?s <urn:absent> ?o }"
            : "CONSTRUCT { <urn:s> <urn:link> _:b . _:b <urn:text> 'A<&😀'@en ; <urn:n> 42 . <urn:s> <urn:iri> <http://example.org/a%2Fb> } WHERE {}";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sparql?query=" + Uri.EscapeDataString(query));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(format));
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(format, response.Content.Headers.ContentType?.MediaType);
        Assert.IsTrue(response.Headers.Vary.Contains("Accept"));
        using var stream = await response.Content.ReadAsStreamAsync();
        var quads = format switch
        {
            "application/n-triples" => RdfSyntax.ParseNTriples(stream, "protocol"),
            "application/ld+json" => RdfSyntax.ParseJsonLd(stream, "protocol"),
            "application/rdf+xml" => ParseRdfXml(stream),
            _ => RdfSyntax.ParseTurtle(stream, "protocol")
        };
        Assert.AreEqual(empty ? 0 : 4, quads.Count);
        if (!empty)
        {
            var blank = quads.Single(quad => quad.Predicate.Value == "urn:link").Object;
            Assert.IsInstanceOfType<RdfBlankNode>(blank);
            Assert.AreEqual(new RdfLiteral("A<&😀", language: "en"), quads.Single(quad => quad.Subject == blank && quad.Predicate.Value == "urn:text").Object);
            Assert.AreEqual(new RdfLiteral("42", "http://www.w3.org/2001/XMLSchema#integer"), quads.Single(quad => quad.Subject == blank && quad.Predicate.Value == "urn:n").Object);
            Assert.AreEqual(new RdfIri("http://example.org/a%2Fb"), quads.Single(quad => quad.Predicate.Value == "urn:iri").Object);
        }
    }

    private static IReadOnlyList<RdfQuad> ParseRdfXml(Stream stream)
    {
        using var graph = new VDS.RDF.Graph();
        using var reader = new StreamReader(stream);
        new VDS.RDF.Parsing.RdfXmlParser().Load(graph, reader);
        return graph.Triples.Select(triple => new RdfQuad(
            RdfSyntax.FromNode(triple.Subject, "protocol"),
            (RdfIri)RdfSyntax.FromNode(triple.Predicate, "protocol"),
            RdfSyntax.FromNode(triple.Object, "protocol"))).ToArray();
    }

    [TestMethod]
    [DataRow("urn:123", "'value'")]
    [DataRow("http://www.w3.org/1999/02/22-rdf-syntax-ns#li", "'value'")]
    [DataRow("http://www.w3.org/1999/02/22-rdf-syntax-ns#about", "'value'")]
    [DataRow("urn:p", "'\\u0001'")]
    public async Task Sparql_rdf_xml_unrepresentable_graphs_fail_without_partial_success(string predicate, string value)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string query = $"CONSTRUCT {{ <urn:s> <urn:valid> 'first' ; <{predicate}> {value} }} WHERE {{}}";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sparql?query=" + Uri.EscapeDataString(query));
        request.Headers.Accept.ParseAdd("application/rdf+xml");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task Sparql_rdf_xml_preserves_xml_literals_whitespace_and_unusual_predicates()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string query = "CONSTRUCT { <urn:s> <urn:123name> 'a\\rb\\nc\\td' ; <urn:xml> '<b>text</b>'^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#XMLLiteral> ; <http://www.w3.org/1999/02/22-rdf-syntax-ns#_1> '' } WHERE {}";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sparql?query=" + Uri.EscapeDataString(query));
        request.Headers.Accept.ParseAdd("application/rdf+xml");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var stream = await response.Content.ReadAsStreamAsync();
        var quads = ParseRdfXml(stream);
        Assert.AreEqual(3, quads.Count);
        Assert.AreEqual("a\rb\nc\td", ((RdfLiteral)quads.Single(q => q.Predicate.Value == "urn:123name").Object).LexicalForm);
        Assert.AreEqual(new RdfLiteral("<b>text</b>", "http://www.w3.org/1999/02/22-rdf-syntax-ns#XMLLiteral"), quads.Single(q => q.Predicate.Value == "urn:xml").Object);
        Assert.AreEqual("", ((RdfLiteral)quads.Single(q => q.Predicate.Value.EndsWith("#_1", StringComparison.Ordinal)).Object).LexicalForm);
    }
}
