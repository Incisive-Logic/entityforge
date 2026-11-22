using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    private const string ModifierRows = "{ VALUES (?rank ?x ?hidden) { (1 'b' 5) (3 'a' 4) (3 'a' 3) (2 UNDEF 2) (1 'b' 1) } }";

    [TestMethod]
    [DataRow("", "", "b,b,-,a,a")]
    [DataRow("", "OFFSET 0", "b,b,-,a,a")]
    [DataRow("", "OFFSET 2 LIMIT 2", "-,a")]
    [DataRow("", "LIMIT 2 OFFSET 1", "b,-")]
    [DataRow("", "LIMIT 0", "")]
    [DataRow("", "OFFSET 9", "")]
    [DataRow("DISTINCT", "", "b,-,a")]
    [DataRow("DISTINCT", "OFFSET 1 LIMIT 2", "-,a")]
    [DataRow("DISTINCT", "OFFSET 2 LIMIT 1", "a")]
    [DataRow("DISTINCT", "OFFSET 3 LIMIT 1", "")]
    public async Task Sparql_projection_duplicates_and_slice_compose_in_select_and_update(
        string modifier, string slice, string expected)
    {
        string[] expectedRows = expected.Length == 0 ? [] : expected.Split(',');
        string query = "SELECT " + modifier + " ?x " + ModifierRows + " ORDER BY ?rank " + slice;
        string directory = CreateDirectory();
        Guid operation = Guid.NewGuid();
        string update = "INSERT { _:row <urn:marker> true; <urn:value> ?x; <urn:hidden> ?rank } WHERE { { " + query + " } }";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using var database = await OpenAsync(directory);
            var rows = RdfSparql.Execute(database, query);
            CollectionAssert.AreEqual(expectedRows, rows.Select(row => row.TryGetValue("x", out var value)
                ? ((RdfLiteral)value).LexicalForm : "-").ToArray());
            Assert.IsTrue(rows.All(row => row.Keys.All(key => key == "x")));
            var nested = RdfSparql.Execute(database, "SELECT * { { " + query + " } }");
            CollectionAssert.AreEquivalent(expectedRows, nested.Select(row => row.TryGetValue("x", out var value)
                ? ((RdfLiteral)value).LexicalForm : "-").ToArray());
            Assert.IsTrue(nested.All(row => row.Keys.All(key => key == "x")));
            await database.CommitSparqlUpdateAsync(operation, update);
            await database.CommitSparqlUpdateAsync(operation, update);
            var quads = database.Find(new()).ToArray();
            Assert.AreEqual(expectedRows.Length, quads.Count(quad => quad.Predicate == new RdfIri("urn:marker")));
            Assert.AreEqual(0, quads.Count(quad => quad.Predicate == new RdfIri("urn:hidden")));
            CollectionAssert.AreEquivalent(expectedRows.Where(value => value != "-").ToArray(),
                quads.Where(quad => quad.Predicate == new RdfIri("urn:value"))
                    .Select(quad => ((RdfLiteral)quad.Object).LexicalForm).ToArray());
        }
    }

    [TestMethod]
    public async Task Sparql_reduced_retains_each_mapping_within_original_bag_cardinality()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var rows = RdfSparql.Execute(database, "SELECT REDUCED ?x " + ModifierRows + " ORDER BY ?rank");
        var counts = rows.GroupBy(row => row.TryGetValue("x", out var value)
            ? ((RdfLiteral)value).LexicalForm : "-").ToDictionary(group => group.Key, group => group.Count());
        CollectionAssert.AreEquivalent(new[] { "a", "b", "-" }, counts.Keys.ToArray());
        Assert.IsTrue(counts["a"] is >= 1 and <= 2);
        Assert.IsTrue(counts["b"] is >= 1 and <= 2);
        Assert.AreEqual(1, counts["-"]);
        Assert.IsTrue(rows.All(row => row.Keys.All(key => key == "x")));
    }
}
