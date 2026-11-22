using System.Globalization;
using System.Text;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfJsonLiteralNumberTests
{
    // Numeric reference vectors from RFC 8785 Appendix B.
    [TestMethod]
    [DataRow("0000000000000000", "0")]
    [DataRow("8000000000000000", "0")]
    [DataRow("0000000000000001", "5e-324")]
    [DataRow("8000000000000001", "-5e-324")]
    [DataRow("7fefffffffffffff", "1.7976931348623157e+308")]
    [DataRow("ffefffffffffffff", "-1.7976931348623157e+308")]
    [DataRow("4340000000000000", "9007199254740992")]
    [DataRow("c340000000000000", "-9007199254740992")]
    [DataRow("4430000000000000", "295147905179352830000")]
    [DataRow("44b52d02c7e14af5", "9.999999999999997e+22")]
    [DataRow("44b52d02c7e14af6", "1e+23")]
    [DataRow("44b52d02c7e14af7", "1.0000000000000001e+23")]
    [DataRow("444b1ae4d6e2ef4e", "999999999999999700000")]
    [DataRow("444b1ae4d6e2ef4f", "999999999999999900000")]
    [DataRow("444b1ae4d6e2ef50", "1e+21")]
    [DataRow("3eb0c6f7a0b5ed8c", "9.999999999999997e-7")]
    [DataRow("3eb0c6f7a0b5ed8d", "0.000001")]
    [DataRow("41b3de4355555553", "333333333.3333332")]
    [DataRow("41b3de4355555554", "333333333.33333325")]
    [DataRow("41b3de4355555555", "333333333.3333333")]
    [DataRow("41b3de4355555556", "333333333.3333334")]
    [DataRow("41b3de4355555557", "333333333.33333343")]
    [DataRow("becbf647612f3696", "-0.0000033333333333333333")]
    [DataRow("43143ff3c1cb0959", "1424953923781206.2")]
    public void JsonLd_json_number_uses_canonical_binary64_spelling(string bits, string expected)
    {
        double value = BitConverter.Int64BitsToDouble(unchecked((long)UInt64.Parse(bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        Assert.AreEqual(expected, RdfJsonNumber.Format(value));
        Assert.AreEqual(expected, Parse(value.ToString("R", CultureInfo.InvariantCulture)).LexicalForm);
    }

    [TestMethod]
    [DataRow("9007199254740993", "9007199254740992")]
    [DataRow("1000000000000000000000000000000", "1e+30")]
    [DataRow("0.0000010", "0.000001")]
    [DataRow("-0.0", "0")]
    [DataRow("1E-7", "1e-7")]
    public void JsonLd_json_number_normalizes_integer_and_float_tokens(string input, string expected)
    {
        Assert.AreEqual(expected, Parse(input).LexicalForm);
    }

    [TestMethod]
    [DataRow("1e400")]
    [DataRow("-1e400")]
    public void JsonLd_json_number_rejects_nonfinite_binary64(string input)
    {
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(input));
    }

    private static RdfLiteral Parse(string number)
    {
        string source = "{\"@id\":\"urn:s\",\"urn:p\":{\"@type\":\"@json\",\"@value\":" + number + "}}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        var quad = RdfSyntax.ParseJsonLd(stream, "numbers").Single();
        var value = (RdfLiteral)quad.Object;
        Assert.AreEqual("http://www.w3.org/1999/02/22-rdf-syntax-ns#JSON", value.Datatype);
        return value;
    }
}
