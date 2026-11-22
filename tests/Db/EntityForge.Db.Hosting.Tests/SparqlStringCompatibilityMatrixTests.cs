using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("STRSTARTS")]
    [DataRow("STRENDS")]
    [DataRow("CONTAINS")]
    [DataRow("STRBEFORE")]
    [DataRow("STRAFTER")]
    public async Task Sparql_string_argument_compatibility_matrix_survives_binding_update_and_restart(string function)
    {
        const string xsd = "http://www.w3.org/2001/XMLSchema#";
        string[] suffixes = ["", "^^<" + xsd + "string>", "@en", "@EN", "@fr", "^^<urn:custom>"];
        // Plain/xsd:string pairs; tagged left with untagged right; matching tags.
        HashSet<(int, int)> admitted = [(0, 0), (0, 1), (1, 0), (1, 1),
            (2, 0), (2, 1), (3, 0), (3, 1), (4, 0), (4, 1),
            (2, 2), (2, 3), (3, 2), (3, 3), (4, 4)];
        var expected = new Dictionary<RdfTerm, RdfTerm>();
        var values = new List<string>();
        for (int left = 0; left < suffixes.Length; left++)
        {
            for (int right = 0; right < suffixes.Length; right++)
            {
                string identity = $"urn:pair:{left}:{right}";
                values.Add($"(<{identity}> 'aba'{suffixes[left]} 'a'{suffixes[right]})");
                if (admitted.Contains((left, right)))
                {
                    string? language = left is 2 or 3 ? "en" : left == 4 ? "fr" : null;
                    expected.Add(new RdfIri(identity), function is "STRBEFORE" or "STRAFTER"
                        ? new RdfLiteral(function == "STRBEFORE" ? "" : "ba", language: language)
                        : new RdfLiteral("true", xsd + "boolean"));
                }
            }
        }
        string group = "VALUES (?s ?left ?right) { " + String.Join(" ", values) +
            " } BIND(" + function + "(?left, ?right) AS ?answer)";
        string directory = CreateDirectory();
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await OpenAsync(directory);
            if (restart == 0)
            {
                var bindings = RdfSparql.Execute(database, "SELECT ?s ?answer (COALESCE(?answer, 'fallback') AS ?fallback) { " + group + " }");
                Assert.AreEqual(36, bindings.Count);
                foreach (var row in bindings)
                {
                    bool valid = expected.TryGetValue(row["s"], out var result);
                    Assert.AreEqual(valid, row.ContainsKey("answer"), row["s"].ToString());
                    Assert.AreEqual(valid ? result : new RdfLiteral("fallback"), row["fallback"]);
                    if (valid)
                    {
                        Assert.AreEqual(result, row["answer"]);
                    }
                }
                Assert.AreEqual(15, RdfSparql.Execute(database, "SELECT ?s { " + group + " FILTER(BOUND(?answer)) }").Count);
                await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT { ?s <urn:answer> ?answer } WHERE { " + group + " }");
            }
            var stored = database.Find(new()).ToArray();
            Assert.AreEqual(expected.Count, stored.Length);
            foreach (var quad in stored)
            {
                Assert.AreEqual(expected[quad.Subject], quad.Object);
            }
        }
    }
}
