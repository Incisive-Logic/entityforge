namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("Infinity")]
    [DataRow("-Infinity")]
    [DataRow("nan")]
    [DataRow("NAN")]
    [DataRow("+INF")]
    [DataRow("\u00a01.0")]
    [DataRow("1e")]
    public void Upstream_floating_oracle_preserves_invalid_lexical_forms(string source)
    {
        Assert.IsNull(NormalizeUpstreamFloating(source, false));
        Assert.IsNull(NormalizeUpstreamFloating(source, true));
    }

    [TestMethod]
    [DataRow("INF", "INF")]
    [DataRow("-INF", "-INF")]
    [DataRow("NaN", "NaN")]
    [DataRow(" +001.25e+2\t", "125")]
    [DataRow("1e999", "INF")]
    [DataRow("-1e999", "-INF")]
    public void Upstream_floating_oracle_normalizes_declared_values(string source, string expected)
    {
        Assert.AreEqual(expected, NormalizeUpstreamFloating(source, false));
        Assert.AreEqual(expected, NormalizeUpstreamFloating(source, true));
    }

    [TestMethod]
    public void Upstream_floating_oracle_keeps_binary32_and_binary64_rounding_separate()
    {
        Assert.AreEqual("16777216", NormalizeUpstreamFloating("16777217", true));
        Assert.AreEqual("16777217", NormalizeUpstreamFloating("16777217", false));
    }
}
