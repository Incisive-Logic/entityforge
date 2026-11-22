using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfLiteralOrderConsistencyTests
{
    private const string Xsd = "http://www.w3.org/2001/XMLSchema#";

    [TestMethod]
    [DataRow("invalid-numeric")]
    [DataRow("mixed-types")]
    [DataRow("promotion")]
    [DataRow("invalid-date")]
    public void Rdf_literal_order_obeys_comparator_laws(string scenario)
    {
        RdfLiteral Literal(string value, string type) => new(value, Xsd + type);
        RdfTerm[] values = scenario switch
        {
            "invalid-numeric" => [Literal("2", "integer"), Literal("10", "integer"), Literal("1x", "integer")],
            "mixed-types" => [Literal("2", "integer"), Literal("3", "float"), Literal("opaque", "g")],
            "promotion" => [Literal("9007199254740992", "integer"), Literal("9007199254740992", "double"), Literal("9007199254740993", "integer")],
            _ => [Literal("2000-01-01T00:00:00Z", "dateTime"), Literal("1999-12-31T19:00:00-05:00", "dateTime"), Literal("1999-z", "dateTime")]
        };
        var comparer = RdfSparql.RdfTermOrderComparer.Instance;
        foreach (var a in values)
        {
            foreach (var b in values)
            {
                Assert.AreEqual(Math.Sign(comparer.Compare(a, b)), -Math.Sign(comparer.Compare(b, a)));
                foreach (var c in values)
                {
                    if (comparer.Compare(a, b) <= 0 && comparer.Compare(b, c) <= 0)
                    {
                        Assert.IsTrue(comparer.Compare(a, c) <= 0, $"{a}; {b}; {c}");
                    }
                }
            }
        }
    }
}
