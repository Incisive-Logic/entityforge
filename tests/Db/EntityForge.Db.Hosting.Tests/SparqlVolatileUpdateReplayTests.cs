using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public async Task Sparql_generated_update_values_remain_stable_on_retry_and_restart()
    {
        const string query = "INSERT { ?id <urn:text> ?text; <urn:random> ?random; <urn:copy> ?random } WHERE { " +
            "VALUES ?n { 1 2 3 } BIND(UUID() AS ?id) BIND(STRUUID() AS ?text) BIND(RAND() AS ?random) }";
        string directory = CreateDirectory();
        Guid operation = Guid.NewGuid();
        RdfQuad[]? original = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await OpenAsync(directory);
            if (restart == 0)
            {
                await database.CommitSparqlUpdateAsync(operation, query);
                original = database.Find(new()).ToArray();
            }
            var version = database.CurrentVersion;
            await database.CommitSparqlUpdateAsync(operation, query);
            Assert.AreEqual(version, database.CurrentVersion);
            CollectionAssert.AreEquivalent(original!, database.Find(new()).ToArray());
            Assert.AreEqual(9, original!.Length);
            var identifiers = new HashSet<Guid>();
            foreach (var group in original.GroupBy(quad => quad.Subject))
            {
                string iri = ((RdfIri)group.Key).Value;
                StringAssert.StartsWith(iri, "urn:uuid:");
                Assert.IsTrue(Guid.TryParseExact(iri[9..], "D", out var id));
                Assert.AreNotEqual(Guid.Empty, id);
                Assert.IsTrue(identifiers.Add(id));
                var text = (RdfLiteral)group.Single(quad => quad.Predicate.Value == "urn:text").Object;
                Assert.AreEqual(RdfLiteral.StringDatatype, text.Datatype);
                Assert.IsTrue(Guid.TryParseExact(text.LexicalForm, "D", out var textId));
                Assert.AreNotEqual(Guid.Empty, textId);
                Assert.IsTrue(identifiers.Add(textId));
                var number = (RdfLiteral)group.Single(quad => quad.Predicate.Value == "urn:random").Object;
                Assert.AreEqual("http://www.w3.org/2001/XMLSchema#double", number.Datatype);
                double value = Double.Parse(number.LexicalForm, System.Globalization.CultureInfo.InvariantCulture);
                Assert.IsTrue(value >= 0 && value < 1);
                Assert.AreEqual(number, group.Single(quad => quad.Predicate.Value == "urn:copy").Object);
            }
            Assert.AreEqual(6, identifiers.Count);
            if (restart == 1)
            {
                await database.CommitSparqlUpdateAsync(Guid.NewGuid(), query);
                Assert.AreEqual(18, database.Find(new()).Count());
                Assert.AreEqual(6, database.Find(new()).Select(quad => quad.Subject).Distinct().Count());
            }
        }
    }
}
