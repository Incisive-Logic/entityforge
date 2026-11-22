using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Sparql_sample_prefers_defined_values_without_duplicating_groups_across_restart(bool reciprocal, bool distinct)
    {
        string expression = reciprocal ? "1/?v" : "IF(?v=0,?missing,?v)";
        string query = "SELECT ?g (COUNT(*) AS ?rows) (COUNT(" + expression + ") AS ?defined) " +
            "(SAMPLE(" + (distinct ? "DISTINCT " : "") + expression + ") AS ?sample) WHERE { " +
            "VALUES (?g ?v) { ('a' UNDEF) ('a' 0) ('a' 2) ('a' 2) ('b' UNDEF) ('b' 0) ('c' 4) } } GROUP BY ?g";
        string update = "INSERT { ?subject <urn:rows> ?rows; <urn:defined> ?defined; <urn:sample> ?sample } " +
            "WHERE { { " + query + " } BIND(IRI(CONCAT('urn:sample:',?g)) AS ?subject) }";
        string directory = CreateDirectory();
        Guid request = Guid.NewGuid();
        RdfSparqlUpdateCommit? receipt = null;
        var expected = new List<RdfQuad>();
        foreach (var (group, rows, defined, sample) in new[]
        {
            ("a", "4", "2", reciprocal ? "0.5" : "2"),
            ("b", "2", "0", (string?)null),
            ("c", "1", "1", reciprocal ? "0.25" : "4")
        })
        {
            var subject = new RdfIri("urn:sample:" + group);
            expected.Add(new(subject, new("urn:rows"), Integer(rows)));
            expected.Add(new(subject, new("urn:defined"), Integer(defined)));
            if (sample is not null)
            {
                expected.Add(new(subject, new("urn:sample"), Sample(sample)));
            }
        }
        for (int round = 0; round < 2; round++)
        {
            await using var database = await OpenAsync(directory);
            var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
            Assert.AreEqual(3, result.Bindings.Count);
            var groups = result.Bindings.ToDictionary(row => ((RdfLiteral)row["g"]).LexicalForm);
            foreach (string group in new[] { "a", "b", "c" })
            {
                var row = groups[group];
                Assert.AreEqual(group == "b" ? 3 : 4, row.Count);
                foreach (var quad in expected.Where(quad => quad.Subject == new RdfIri("urn:sample:" + group)))
                {
                    string variable = quad.Predicate.Value[4..];
                    Assert.AreEqual(quad.Object, row[variable]);
                }
                if (group == "b")
                {
                    Assert.IsFalse(row.ContainsKey("sample"));
                }
            }
            var committed = await database.CommitSparqlUpdateAsync(request, update);
            receipt ??= committed;
            Assert.AreEqual(receipt, committed);
            CollectionAssert.AreEquivalent(expected.ToArray(), database.Find(new()).ToArray());
        }

        static RdfLiteral Integer(string value) => new(value, "http://www.w3.org/2001/XMLSchema#integer");
        RdfLiteral Sample(string value) => new(value, "http://www.w3.org/2001/XMLSchema#" + (reciprocal ? "decimal" : "integer"));
    }
}
