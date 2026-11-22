using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_numeric_total_order_preserves_full_and_limited_results(bool descending)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string xsd = "http://www.w3.org/2001/XMLSchema#";
        string[] values = ["'-INF'^^<" + xsd + "double>", "0", "9007199254740992e0", "9007199254740993",
            "'INF'^^<" + xsd + "float>", "'NaN'^^<" + xsd + "double>", "'1x'^^<" + xsd + "integer>"];
        var random = new Random(3911);
        string[] expected = Enumerable.Range(0, values.Length).Select(index => index.ToString()).ToArray();
        if (descending)
        {
            Array.Reverse(expected);
        }
        for (int permutation = 0; permutation < 8; permutation++)
        {
            int[] order = Enumerable.Range(0, values.Length).ToArray();
            random.Shuffle(order);
            string bindings = String.Join(" ", order.Select(index => "('" + index + "' " + values[index] + ")"));
            string body = " WHERE { VALUES (?id ?v) { " + bindings + " } }";
            string query = "SELECT ?id" + body + (descending ? " ORDER BY DESC(?v)" : " ORDER BY ?v");
            foreach (int count in new[] { 1, 3, 7 })
            {
                var rows = RdfSparql.Execute(database, query + " LIMIT " + count);
                CollectionAssert.AreEqual(expected.Take(count).ToArray(), rows.Select(row => ((RdfLiteral)row["id"]).LexicalForm).ToArray());
            }
            var full = RdfSparql.Execute(database, query);
            CollectionAssert.AreEqual(expected, full.Select(row => ((RdfLiteral)row["id"]).LexicalForm).ToArray());
        }
    }
}
