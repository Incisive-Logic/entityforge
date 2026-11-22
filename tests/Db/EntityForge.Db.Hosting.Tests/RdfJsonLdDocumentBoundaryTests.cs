using System.Text;
using EntityForge.Db.Rdf;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfJsonLdDocumentBoundaryTests
{
    [TestMethod]
    [DataRow(" {}")]
    [DataRow(" []")]
    [DataRow(" true")]
    [DataRow(" trailing")]
    [DataRow(" // comment")]
    [DataRow(" /* comment */")]
    public void JsonLd_rejects_content_after_the_document(string suffix)
    {
        const string document = "{\"@id\":\"urn:s\",\"urn:p\":[{\"@id\":\"urn:o\"}]}";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(document + suffix));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSyntax.ParseJsonLd(input, "scope"));
        Assert.IsTrue(input.CanRead);
    }

    [TestMethod]
    [DataRow("{'@id':'urn:s','urn:p':{'@id':'urn:o'}}")]
    [DataRow("{\"@id\":\"urn:s\",\"urn:p\":{\"@id\":\"urn:o\"},}")]
    [DataRow("{/* comment */\"@id\":\"urn:s\",\"urn:p\":{\"@id\":\"urn:o\"}}")]
    [DataRow("{\"@id\":\"urn:s\",\"urn:p\":NaN}")]
    [DataRow("{\"@id\":\"urn:s\",\"urn:p\":undefined}")]
    [DataRow("{value:42}")]
    public void JsonLd_rejects_non_json_syntax(string source)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(source));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSyntax.ParseJsonLd(input, "scope"));
        Assert.IsTrue(input.CanRead);
    }

    [TestMethod]
    public void JsonLd_accepts_surrounding_json_whitespace()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(" \t\r\n{\"@id\":\"urn:s\",\"urn:p\":[{\"@id\":\"urn:o\"}]} \t\r\n"));
        Assert.AreEqual(1, RdfSyntax.ParseJsonLd(input, "scope").Count);
        Assert.IsTrue(input.CanRead);
    }
}
