using System.Text;
using System.Text.Json;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfStructuredIriIdentityTests
{
    [TestMethod]
    public void JsonLd_context_graph_predicate_and_datatype_preserve_generic_iris_and_literal_text()
    {
        const string source = """
            {
              "@context": { "value": { "@id": "g:p", "@type": "g:t" } },
              "@id": "g:graph",
              "@graph": [
                { "@id": "g:s", "value": ["g:h", "2026-11-17T12:00:00Z"] }
              ]
            }
            """;
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(source));
        var quads = RdfSyntax.ParseJsonLd(input, "scope");
        Assert.AreEqual(2, quads.Count);
        foreach (var quad in quads)
        {
            Assert.AreEqual(new RdfIri("g:graph"), quad.Graph);
            Assert.AreEqual(new RdfIri("g:s"), quad.Subject);
            Assert.AreEqual(new RdfIri("g:p"), quad.Predicate);
            Assert.AreEqual("g:t", ((RdfLiteral)quad.Object).Datatype);
        }
        CollectionAssert.AreEquivalent(new[] { "g:h", "2026-11-17T12:00:00Z" },
            quads.Select(quad => ((RdfLiteral)quad.Object).LexicalForm).ToArray());
        Assert.IsTrue(input.CanRead);
    }

    [TestMethod]
    [DataRow("http://EXAMPLE.test:80/a/../item")]
    [DataRow("http://example.test/item")]
    [DataRow("g:h")]
    [DataRow("http://[v1.a]/item")]
    public void JsonLd_expanded_import_preserves_absolute_iri_identity(string iri)
    {
        string source = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@id"] = iri,
            ["urn:p"] = new[] { new Dictionary<string, string> { ["@id"] = iri } }
        });
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(source));
        var quad = RdfSyntax.ParseJsonLd(input, "scope").Single();
        Assert.AreEqual(new RdfIri(iri), quad.Subject);
        Assert.AreEqual(new RdfIri(iri), quad.Object);
    }

    [TestMethod]
    [DataRow("http://EXAMPLE.test:80/a/../item")]
    [DataRow("http://example.test/item")]
    [DataRow("g:h")]
    [DataRow("http://[v1.a]/item")]
    public void RdfXml_loaded_import_preserves_absolute_iri_identity(string iri)
    {
        string source = "<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#' xmlns:p='urn:p:'>" +
            "<rdf:Description rdf:about='" + iri + "'><p:value rdf:resource='" + iri + "'/></rdf:Description></rdf:RDF>";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(source));
        var quad = RdfSyntax.ParseLoadedGraph(input, "application/rdf+xml", new Uri("https://example.test/source"), "scope", default).Single();
        Assert.AreEqual(new RdfIri(iri), quad.Subject);
        Assert.AreEqual(new RdfIri(iri), quad.Object);
    }

    [TestMethod]
    public void RdfXml_nested_bases_and_unicode_ID_preserve_native_identity()
    {
        const string source = """
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:p="urn:p:"
                     xml:base="http://EXAMPLE.test:80/a/">
              <rdf:Description rdf:ID="Dürst" xml:base="../b/">
                <p:value rdf:resource="./c"/>
                <p:typed rdf:datatype="g:t">  content  </p:typed>
              </rdf:Description>
            </rdf:RDF>
            """;
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(source));
        var quads = RdfSyntax.ParseLoadedGraph(input, "application/rdf+xml", new Uri("https://example.test/source"), "scope", default);
        Assert.AreEqual(2, quads.Count);
        Assert.IsTrue(quads.All(quad => quad.Subject == new RdfIri("http://EXAMPLE.test:80/b/#Dürst")));
        Assert.IsTrue(quads.Any(quad => quad.Object == new RdfIri("http://EXAMPLE.test:80/b/c")));
        Assert.IsTrue(quads.Any(quad => quad.Object == new RdfLiteral("  content  ", "g:t")));
    }

    [TestMethod]
    public void RdfXml_namespace_names_and_type_attributes_preserve_identity_without_changing_literals()
    {
        const string source = """
            <efiri0:Thing xmlns:efiri0="http://EXAMPLE.test:80/#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                rdf:about="g:s" rdf:type="g:t" efiri0:label="g:t">
              <efiri0:link rdf:resource="g:o"/>
              <rdf:type>g:t</rdf:type>
            </efiri0:Thing>
            """;
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(source));
        var quads = RdfSyntax.ParseLoadedGraph(input, "application/rdf+xml", new Uri("https://example.test/source"), "scope", default);
        Assert.AreEqual(5, quads.Count);
        Assert.IsTrue(quads.All(quad => quad.Subject == new RdfIri("g:s")));
        Assert.IsTrue(quads.Any(quad => quad.Object == new RdfIri("http://EXAMPLE.test:80/#Thing")));
        Assert.IsTrue(quads.Any(quad => quad.Object == new RdfIri("g:t")));
        Assert.IsTrue(quads.Any(quad => quad.Predicate == new RdfIri("http://EXAMPLE.test:80/#label") && quad.Object == new RdfLiteral("g:t")));
        Assert.IsTrue(quads.Any(quad => quad.Predicate == new RdfIri("http://EXAMPLE.test:80/#link") && quad.Object == new RdfIri("g:o")));
        Assert.IsTrue(quads.Any(quad => quad.Predicate == new RdfIri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type") && quad.Object == new RdfLiteral("g:t")));
    }

    [TestMethod]
    [DataRow("Literal")]
    [DataRow("custom")]
    public void RdfXml_literal_content_keeps_namespaces_attributes_and_whitespace(string parseType)
    {
        string source = """
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:p="urn:p:"
                xmlns:x="urn:literal:" xml:base="http://EXAMPLE.test:80/">
              <rdf:Description rdf:about="g:s">
                <p:markup rdf:parseType="TYPE"> before <x:item rdf:about="g:h" xml:base="g:b"/> after </p:markup>
              </rdf:Description>
            </rdf:RDF>
            """;
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(source.Replace("TYPE", parseType, StringComparison.Ordinal)));
        var quads = RdfSyntax.ParseLoadedGraph(input, "application/rdf+xml", new Uri("https://example.test/source"), "scope", default);
        Assert.AreEqual(1, quads.Count);
        var literal = (RdfLiteral)quads[0].Object;
        Assert.AreEqual("http://www.w3.org/1999/02/22-rdf-syntax-ns#XMLLiteral", literal.Datatype);
        Assert.IsTrue(literal.LexicalForm.StartsWith(" before ", StringComparison.Ordinal));
        Assert.IsTrue(literal.LexicalForm.EndsWith(" after ", StringComparison.Ordinal));
        Assert.IsTrue(literal.LexicalForm.Contains("urn:literal:", StringComparison.Ordinal));
        Assert.IsTrue(literal.LexicalForm.Contains("rdf:about=\"g:h\"", StringComparison.Ordinal));
        Assert.IsTrue(literal.LexicalForm.Contains("xml:base=\"g:b\"", StringComparison.Ordinal));
        Assert.IsFalse(literal.LexicalForm.Contains("entityforge.invalid", StringComparison.Ordinal));
        Assert.IsTrue(literal.LexicalForm.Contains("</x:item>", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("duplicate")]
    [DataRow("invalid ID")]
    public void RdfXml_ID_validation_survives_native_iri_binding(string id)
    {
        string source = "<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>" +
            "<rdf:Description rdf:ID='" + id + "'/><rdf:Description rdf:ID='" + id + "'/></rdf:RDF>";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(source));
        Assert.Throws<Exception>(() => RdfSyntax.ParseLoadedGraph(input, "application/rdf+xml",
            new Uri("https://example.test/source"), "scope", default));
    }
}
