using System.Text;
using EntityForge.Db.Rdf;
using VDS.RDF;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RdfImportUriFactoryIsolationTests
{
    [TestMethod]
    [DataRow("turtle")]
    [DataRow("ntriples")]
    [DataRow("nquads")]
    [DataRow("jsonld")]
    [DataRow("load-turtle")]
    [DataRow("load-ntriples")]
    [DataRow("load-xml")]
    public void Rdf_import_does_not_use_process_global_uri_interning(string format)
    {
        string source = format switch
        {
            "jsonld" => "{\"@id\":\"urn:s\",\"urn:p\":[{\"@id\":\"urn:o\"}]}",
            "load-xml" => "<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#' xmlns:p='urn:p:'><rdf:Description rdf:about='urn:s'><p:value rdf:resource='urn:o'/></rdf:Description></rdf:RDF>",
            _ => "<urn:s> <urn:p> <urn:o> ."
        };
        var previous = UriFactory.Root;
        try
        {
            UriFactory.Root = new RejectingUriFactory();
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(source));
            var quads = format switch
            {
                "turtle" => RdfSyntax.ParseTurtle(input, "scope"),
                "ntriples" => RdfSyntax.ParseNTriples(input, "scope"),
                "nquads" => RdfSyntax.ParseNQuads(input, "scope"),
                "jsonld" => RdfSyntax.ParseJsonLd(input, "scope"),
                _ => RdfSyntax.ParseLoadedGraph(input, format switch
                {
                    "load-turtle" => "text/turtle",
                    "load-ntriples" => "application/n-triples",
                    _ => "application/rdf+xml"
                }, new Uri("https://example.test/source"), "scope", default)
            };
            Assert.AreEqual(1, quads.Count);
        }
        finally
        {
            UriFactory.Root = previous;
        }
    }

    private sealed class RejectingUriFactory : IUriFactory
    {
        public bool InternUris
        {
            get; set;
        }

        public Uri Create(string uri) => throw new InvalidOperationException("Import used the global URI factory.");

        public Uri Create(Uri baseUri, string relativeUri) => throw new InvalidOperationException("Import used the global URI factory.");

        public bool TryGetUri(string uri, out Uri value) => throw new InvalidOperationException("Import queried the global URI cache.");

        public void Clear() => throw new InvalidOperationException("Import cleared the global URI cache.");
    }
}
