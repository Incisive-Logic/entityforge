using System.Text;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfImportIriIdentityTests
{
    [TestMethod]
    [DataRow("g:h")]
    [DataRow("http:g")]
    [DataRow("http://[v1.a]/item")]
    [DataRow("http://example.test:99999/item")]
    [DataRow("https://example.test/\u00A0item")]
    public void Rdf_line_import_preserves_generic_iris_in_every_term_position(string iri)
    {
        foreach (bool dataset in new[] { false, true })
        {
            string graph = dataset ? " <" + iri + ">" : "";
            string source = "<" + iri + "> <" + iri + "> <" + iri + ">" + graph + " .\n" +
                "<urn:s> <urn:p> \"text\"^^<" + iri + ">" + graph + " .";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
            var quads = dataset ? RdfSyntax.ParseNQuads(stream, "scope") : RdfSyntax.ParseNTriples(stream, "scope");
            Assert.AreEqual(2, quads.Count);
            Assert.AreEqual(new RdfIri(iri), quads[0].Subject);
            Assert.AreEqual(new RdfIri(iri), quads[0].Predicate);
            Assert.AreEqual(new RdfIri(iri), quads[0].Object);
            Assert.AreEqual(iri, ((RdfLiteral)quads[1].Object).Datatype);
            Assert.AreEqual(dataset ? new RdfIri(iri) : null, quads[0].Graph);
            Assert.AreEqual(dataset ? new RdfIri(iri) : null, quads[1].Graph);
            if (!dataset)
            {
                using var loadedInput = new MemoryStream(Encoding.UTF8.GetBytes(source));
                var loaded = RdfSyntax.ParseLoadedGraph(loadedInput, "application/n-triples", new Uri("https://example.test/"), "scope", default);
                CollectionAssert.AreEqual(quads.ToArray(), loaded.ToArray());
            }
        }
    }

    [TestMethod]
    [DataRow("<relative> <urn:p> <urn:o> .")]
    [DataRow("@base <https://example.test/> . <item> <urn:p> <urn:o> .")]
    [DataRow("@prefix ex: <urn:ex:> . ex:s ex:p ex:o .")]
    [DataRow("<urn:s> <urn:p> 'text' .")]
    [DataRow("<urn:s> <urn:p> \"\"\"text\"\"\" .")]
    public void Rdf_line_import_does_not_accept_turtle_only_syntax(string source)
    {
        using var triples = new MemoryStream(Encoding.UTF8.GetBytes(source));
        using var quads = new MemoryStream(Encoding.UTF8.GetBytes(source));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSyntax.ParseNTriples(triples, "scope"));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSyntax.ParseNQuads(quads, "scope"));
    }

    [TestMethod]
    [DataRow("turtle")]
    [DataRow("ntriples")]
    [DataRow("nquads")]
    public void Rdf_import_preserves_distinct_absolute_iri_spellings(string syntax)
    {
        string[] identifiers = ["http://EXAMPLE.test:80/a/../item", "http://example.test/item",
            "https://example.test/%2f", "https://example.test/%2F"];
        string text = String.Join("\n", identifiers.Select(iri => "<" + iri + "> <urn:p> <urn:o> ."));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var quads = syntax switch
        {
            "turtle" => RdfSyntax.ParseTurtle(stream, "scope"),
            "ntriples" => RdfSyntax.ParseNTriples(stream, "scope"),
            _ => RdfSyntax.ParseNQuads(stream, "scope")
        };
        CollectionAssert.AreEqual(identifiers, quads.Select(quad => ((RdfIri)quad.Subject).Value).ToArray());
    }

    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    [DataRow("\r")]
    [DataRow("\n\r")]
    public void Turtle_import_preserves_base_prefix_literal_and_escape_identity(string newline)
    {
        string text = String.Join(newline,
            "@base <http://EXAMPLE.test:80/a/> .",
            "@prefix ex: <../ns/> . # <not-an-iri>",
            "ex:s ex:p \"\"\"literal <urn:entityforge:turtle-iri:0>",
            "@base untouched\"\"\"^^ex:type .",
            "BASE <../b/>",
            "<item> ex:p ex:escaped\\~ .",
            "PREFIX ex: <https://OTHER.test:443/>",
            "<urn:entityforge:turtle-iri:0> ex:p <https://example.test/\\u0041> .");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var quads = RdfSyntax.ParseTurtle(stream, "scope");
        Assert.AreEqual(3, quads.Count);
        Assert.AreEqual(new RdfIri("http://EXAMPLE.test:80/ns/s"), quads[0].Subject);
        Assert.AreEqual(new RdfIri("http://EXAMPLE.test:80/ns/p"), quads[0].Predicate);
        var literal = (RdfLiteral)quads[0].Object;
        Assert.AreEqual("http://EXAMPLE.test:80/ns/type", literal.Datatype);
        Assert.AreEqual("literal <urn:entityforge:turtle-iri:0>" + newline + "@base untouched", literal.LexicalForm);
        Assert.AreEqual(new RdfIri("http://EXAMPLE.test:80/b/item"), quads[1].Subject);
        Assert.AreEqual(new RdfIri("http://EXAMPLE.test:80/ns/escaped~"), quads[1].Object);
        Assert.AreEqual(new RdfIri("urn:entityforge:turtle-iri:0"), quads[2].Subject);
        Assert.AreEqual(new RdfIri("https://OTHER.test:443/p"), quads[2].Predicate);
        Assert.AreEqual(new RdfIri("https://example.test/A"), quads[2].Object);
    }

    [TestMethod]
    [DataRow("@prefix ex: <urn:ex:> . BASE ex:base ex:s ex:p ex:o .")]
    [DataRow("@prefix ex: <urn:ex:> ex:s ex:p ex:o .")]
    [DataRow("missing:s <urn:p> <urn:o> .")]
    [DataRow("BASE <https://example.test/> <bad%/../item> <urn:p> <urn:o> .")]
    public void Turtle_iri_binding_does_not_legalize_malformed_input(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSyntax.ParseTurtle(stream, "scope"));
    }

    [TestMethod]
    public void Turtle_loaded_graph_preserves_transport_base_and_term_identity()
    {
        const string text = "<item> <http://EXAMPLE.test:80/p> \"value\"^^<http://EXAMPLE.test:80/type> .";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var quad = RdfSyntax.ParseLoadedGraph(stream, "text/turtle", new Uri("http://EXAMPLE.test:80/a/"), "scope", default).Single();
        Assert.AreEqual(new RdfIri("http://EXAMPLE.test:80/a/item"), quad.Subject);
        Assert.AreEqual(new RdfIri("http://EXAMPLE.test:80/p"), quad.Predicate);
        Assert.AreEqual("http://EXAMPLE.test:80/type", ((RdfLiteral)quad.Object).Datatype);
    }
}
