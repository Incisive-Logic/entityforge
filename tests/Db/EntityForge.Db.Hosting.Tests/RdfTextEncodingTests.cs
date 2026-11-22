using System.Text;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfTextEncodingTests
{
    public static IEnumerable<object[]> InvalidInputs()
    {
        foreach (string format in new[] { "turtle", "ntriples", "nquads", "load-turtle", "load-ntriples" })
        {
            foreach (string encoding in new[] { "malformed", "utf16", "utf32" })
            {
                yield return [format, encoding];
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(InvalidInputs))]
    public void Rdf_text_input_rejects_invalid_utf8_without_encoding_detection(string format, string encoding)
    {
        const string document = "<urn:s> <urn:p> \"value\" .";
        byte[] bytes = encoding switch
        {
            "utf16" => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(document)).ToArray(),
            "utf32" => Encoding.UTF32.GetPreamble().Concat(Encoding.UTF32.GetBytes(document)).ToArray(),
            _ => Encoding.UTF8.GetBytes("<urn:s> <urn:p> \"").Concat(new byte[] { 0xC3, 0x28 })
                .Concat(Encoding.UTF8.GetBytes("\" .")).ToArray()
        };
        using var stream = new MemoryStream(bytes);
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(stream, format));
        Assert.IsTrue(stream.CanRead, "The caller owns the input stream.");
    }

    [TestMethod]
    [DataRow("turtle")]
    [DataRow("ntriples")]
    [DataRow("nquads")]
    [DataRow("load-turtle")]
    [DataRow("load-ntriples")]
    public void Rdf_text_input_preserves_valid_utf8_and_optional_utf8_bom(string format)
    {
        const string value = "caf\u00E9 \U0001F600 \uFFFD";
        byte[] content = Encoding.UTF8.GetBytes("<urn:s> <urn:p> \"" + value + "\" .");
        foreach (bool bom in new[] { false, true })
        {
            using var stream = new MemoryStream(bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(content).ToArray() : content);
            Assert.AreEqual(value, ((RdfLiteral)Parse(stream, format).Single().Object).LexicalForm);
            Assert.IsTrue(stream.CanRead);
        }
    }

    private static IReadOnlyList<RdfQuad> Parse(Stream stream, string format) => format switch
    {
        "turtle" => RdfSyntax.ParseTurtle(stream, "scope"),
        "ntriples" => RdfSyntax.ParseNTriples(stream, "scope"),
        "nquads" => RdfSyntax.ParseNQuads(stream, "scope"),
        _ => RdfSyntax.ParseLoadedGraph(stream, format == "load-turtle" ? "text/turtle" : "application/n-triples",
            new Uri("https://example.test/"), "scope", default)
    };
}
