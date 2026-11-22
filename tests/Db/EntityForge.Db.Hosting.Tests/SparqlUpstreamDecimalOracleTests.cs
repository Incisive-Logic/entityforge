namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("+000123.45000", "123.45")]
    [DataRow("-.000", "0.0")]
    [DataRow("\t.125\r\n", "0.125")]
    [DataRow("42", "42.0")]
    [DataRow("42.", "42.0")]
    [DataRow("1.00000000000000000000000000001", "1.00000000000000000000000000001")]
    [DataRow("999999999999999999999999999999999999999999.000", "999999999999999999999999999999999999999999.0")]
    public void Upstream_decimal_oracle_normalizes_without_precision_loss(string source, string expected) =>
        Assert.AreEqual(expected, NormalizeUpstreamDecimal(source));

    [TestMethod]
    [DataRow("1e2")]
    [DataRow("NaN")]
    [DataRow("INF")]
    [DataRow(".")]
    [DataRow("1 2")]
    [DataRow("\u00a01.0")]
    public void Upstream_decimal_oracle_does_not_repair_invalid_lexical_forms(string source) =>
        Assert.IsNull(NormalizeUpstreamDecimal(source));

    [TestMethod]
    public void Upstream_decimal_oracle_distinguishes_values_beyond_runtime_decimal_precision()
    {
        Assert.AreNotEqual(NormalizeUpstreamDecimal("1.00000000000000000000000000001"),
            NormalizeUpstreamDecimal("1.00000000000000000000000000002"));
    }
}
