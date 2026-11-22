using System.Security.Cryptography;
using System.Text;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    private static string VerifyXPath3RegexFixture(string fragment, byte[] queryBytes)
    {
        // Pin reviewed content, not merely the fixture's name. A changed
        // upstream case requires a fresh scope decision instead of a silent skip.
        string expected = fragment switch
        {
            "#regex-no-metacharacters" => "578C2C013F7ED8B7A56626E55B101E487395A82D86CAF028DD37C7E199235E8E",
            "#regex-no-metacharacters-case-insensitive" => "2560FD171602EF4540DBCB328FC2C90D3ECE5CE08EA7C3C84EB43502637543CF",
            _ => throw new InvalidDataException("The upstream regex fixture has no reviewed exclusion.")
        };
        string actual = Convert.ToHexString(SHA256.HashData(queryBytes));
        if (actual != expected)
        {
            throw new InvalidDataException("The excluded upstream regex fixture changed; review its standards scope before excluding it.");
        }
        return actual;
    }

    [TestMethod]
    [DataRow("#regex-no-metacharacters")]
    [DataRow("#regex-no-metacharacters-case-insensitive")]
    [DataRow("#unknown")]
    public void Sparql_upstream_disposition_refuses_changed_or_unreviewed_fixture(string fragment)
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            VerifyXPath3RegexFixture(fragment, Encoding.UTF8.GetBytes("SELECT * WHERE {}")));
    }
}
