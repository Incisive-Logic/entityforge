using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    // SPARQL 1.1 Query section 17.5: one representative for each of the
    // nine source categories and seven required constructor targets.
    // These explicit expected values are independent of the cast evaluator.
    public static IEnumerable<object[]> ConstructorMatrix()
    {
        string[] targets = ["string", "float", "double", "decimal", "integer", "dateTime", "boolean"];
        (string Source, string?[] Expected)[] rows =
        [
            ("'1'^^xsd:string", ["1", "1", "1", "1", "1", null, "true"]),
            ("'1'^^xsd:float", ["1", "1", "1", "1", "1", null, "true"]),
            ("'1'^^xsd:double", ["1", "1", "1", "1", "1", null, "true"]),
            ("'1'^^xsd:decimal", ["1", "1", "1", "1", "1", null, "true"]),
            ("'1'^^xsd:integer", ["1", "1", "1", "1", "1", null, "true"]),
            ("'2024-02-29T12:00:00Z'^^xsd:dateTime",
                ["2024-02-29T12:00:00Z", null, null, null, null, "2024-02-29T12:00:00Z", null]),
            ("true", ["true", "1", "1", "1", "1", null, "true"]),
            ("<urn:constructor:source>", ["urn:constructor:source", null, null, null, null, null, null]),
            ("'1'", ["1", "1", "1", "1", "1", null, "true"])
        ];
        foreach (var row in rows)
        {
            for (int column = 0; column < targets.Length; column++)
            {
                yield return [row.Source, targets[column], row.Expected[column]!];
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(ConstructorMatrix))]
    public async Task Sparql_required_constructor_matrix_preserves_values_and_expression_errors(
        string source, string target, string? expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string xsd = "http://www.w3.org/2001/XMLSchema#";
        var row = RdfSparql.Execute(database,
            "PREFIX xsd:<" + xsd + "> SELECT ?bound (xsd:" + target + "(?v) AS ?projected) " +
            "(COALESCE(xsd:" + target + "(?v),'cast-error') AS ?recovered) WHERE { " +
            "VALUES ?v { " + source + " } BIND(xsd:" + target + "(?v) AS ?bound) }").Single();
        foreach (string name in new[] { "bound", "projected" })
        {
            Assert.AreEqual(expected is not null, row.ContainsKey(name), name);
            if (expected is not null)
            {
                Assert.AreEqual(new RdfLiteral(expected, xsd + target), row[name], name);
            }
        }
        Assert.AreEqual(expected is null ? new RdfLiteral("cast-error") : new RdfLiteral(expected, xsd + target),
            row["recovered"]);
    }
}
