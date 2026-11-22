using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("'value'^^<urn:opaque>", "'value'^^<urn:opaque>", true)]
    [DataRow("'value'^^<urn:opaque>", "'other'^^<urn:opaque>", null)]
    [DataRow("'value'^^<urn:opaque>", "'value'^^<urn:other>", null)]
    [DataRow("'1'^^<urn:opaque>", "1", null)]
    [DataRow("'value'^^<urn:opaque>", "'value'", null)]
    [DataRow("'value'^^<urn:opaque>", "<urn:value>", false)]
    public async Task Sparql_opaque_literal_equality_preserves_errors_and_update_bindings_after_restart(
        string first, string second, bool? equal)
    {
        // Unsupported datatypes have no value-comparison extension. Identical
        // RDF terms still compare equal; unequal literals produce an error,
        // whereas a literal and an IRI compare false. Error is not false.
        foreach (bool reverse in new[] { false, true })
        {
            string left = reverse ? second : first;
            string right = reverse ? first : second;
            string bindings = "BIND(" + left + " AS ?a) BIND(" + right + " AS ?b) " +
                "BIND(?a=?b AS ?equal) BIND(?a!=?b AS ?different) BIND(sameTerm(?a,?b) AS ?same)";
            string query = "SELECT ?equal ?different ?same WHERE { " + bindings + " }";
            string update = "INSERT { <urn:comparison> <urn:equal> ?equal; <urn:different> ?different; <urn:same> ?same } WHERE { " + bindings + " }";
            var expected = new List<RdfQuad>
            {
                new(new RdfIri("urn:comparison"), new("urn:same"), BooleanTerm(equal == true))
            };
            if (equal is bool value)
            {
                expected.Add(new(new RdfIri("urn:comparison"), new("urn:equal"), BooleanTerm(value)));
                expected.Add(new(new RdfIri("urn:comparison"), new("urn:different"), BooleanTerm(!value)));
            }
            string directory = CreateDirectory();
            Guid request = Guid.NewGuid();
            RdfSparqlUpdateCommit? receipt = null;
            for (int round = 0; round < 2; round++)
            {
                await using var database = await OpenAsync(directory);
                var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
                Assert.AreEqual(1, result.Bindings.Count);
                var row = result.Bindings[0];
                Assert.AreEqual(equal.HasValue ? 3 : 1, row.Count);
                Assert.AreEqual(BooleanTerm(equal == true), row["same"]);
                if (equal is bool outcome)
                {
                    Assert.AreEqual(BooleanTerm(outcome), row["equal"]);
                    Assert.AreEqual(BooleanTerm(!outcome), row["different"]);
                }
                else
                {
                    Assert.IsFalse(row.ContainsKey("equal"));
                    Assert.IsFalse(row.ContainsKey("different"));
                }
                var committed = await database.CommitSparqlUpdateAsync(request, update);
                receipt ??= committed;
                Assert.AreEqual(receipt, committed);
                CollectionAssert.AreEquivalent(expected.ToArray(), database.Find(new()).ToArray());
            }
        }

        static RdfLiteral BooleanTerm(bool value) => new(value ? "true" : "false", "http://www.w3.org/2001/XMLSchema#boolean");
    }
}
