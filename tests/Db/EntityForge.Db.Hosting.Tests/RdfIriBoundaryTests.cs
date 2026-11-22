using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfIriBoundaryTests
{
    [TestMethod]
    public void Native_iri_rejects_unpaired_surrogates()
    {
        foreach (char value in new[] { (char)0xD800, (char)0xDC00 })
        {
            string iri = "urn:" + value;
            Assert.ThrowsExactly<ArgumentException>(() => new RdfIri(iri));
            Assert.ThrowsExactly<ArgumentException>(() => new RdfLiteral("value", iri));
        }
    }

    [TestMethod]
    [DataRow("relative")]
    [DataRow("1abc:value")]
    [DataRow("http://a@b@c/")]
    [DataRow("http://a:port/")]
    [DataRow("http://[::1%25eth0]/")]
    [DataRow("http://[v.a]/")]
    [DataRow("http://[v1.]/")]
    [DataRow("http://[::ffff:192.168.001.1]/")]
    [DataRow("http://[::1]extra/")]
    [DataRow("urn:a[b]")]
    [DataRow("urn:a b")]
    [DataRow(" urn:a")]
    [DataRow("urn:a ")]
    [DataRow("urn:a\tb")]
    [DataRow("urn:a\nb")]
    [DataRow("urn:a<b")]
    [DataRow("urn:a>b")]
    [DataRow("urn:a\"b")]
    [DataRow("urn:a\\b")]
    [DataRow("urn:a{b}")]
    [DataRow("urn:a|b")]
    [DataRow("urn:a^b")]
    [DataRow("urn:a`b")]
    [DataRow("urn:a%")]
    [DataRow("urn:a%2")]
    [DataRow("urn:a%ZZ")]
    [DataRow("urn:a#b#c")]
    [DataRow("C:\\database\\file")]
    [DataRow("urn:\uFDD0")]
    [DataRow("urn:\uFFF0")]
    [DataRow("urn:\uFFFD")]
    [DataRow("urn:\uE000")]
    [DataRow("urn:a#\uE000")]
    [DataRow("urn:a#?\uE000")]
    [DataRow("urn:\U0001FFFE")]
    [DataRow("urn:\U000E0000")]
    [DataRow("urn:\U000F0000")]
    public void Native_iri_and_datatype_reject_malformed_original_spelling(string value)
    {
        Assert.ThrowsExactly<ArgumentException>(() => new RdfIri(value));
        Assert.ThrowsExactly<ArgumentException>(() => new RdfLiteral("value", value));
    }

    [TestMethod]
    [DataRow("c:/database/file")]
    [DataRow("http://example.test:99999/a")]
    [DataRow("http://[v1.a]/")]
    [DataRow("http://[::ffff:192.168.1.1]/")]
    [DataRow("http://[::1]:/")]
    [DataRow("x://")]
    [DataRow("x://user:pass@host:/a")]
    [DataRow("eXAMPLE://a/./b/../b/%63/%7bfoo%7d#xyz")]
    [DataRow("urn:雪")]
    [DataRow("urn:\u00a0")]
    [DataRow("urn:\U00010000")]
    [DataRow("urn:雪😀")]
    [DataRow("urn:\U0001FFFD")]
    [DataRow("urn:\U000E1000")]
    [DataRow("urn:a?\uE000")]
    [DataRow("urn:a?\U000F0000")]
    [DataRow("urn:a?\U0010FFFD")]
    [DataRow("https://example.test/a%20b")]
    [DataRow("https://example.test/a%2fb")]
    [DataRow("https://example.test/a%2Fb")]
    [DataRow("HTTP://Example.TEST/a/../b#fragment")]
    [DataRow("urn:a?query=value#fragment")]
    [DataRow("urn:")]
    public void Native_iri_validation_preserves_valid_original_spelling(string value)
    {
        Assert.AreEqual(value, new RdfIri(value).Value);
        Assert.AreEqual(value, new RdfLiteral("value", value).Datatype);
    }
}
