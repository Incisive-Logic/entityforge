using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, false, true)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public async Task Sparql_cast_errors_in_modify_preserve_solutions_unless_explicitly_filtered(
        bool namedGraph, bool filterErrors, bool recoverErrors)
    {
        const string xsd = "http://www.w3.org/2001/XMLSchema#";
        string directory = CreateDirectory();
        RdfIri? graph = namedGraph ? new("urn:cast-target") : null;
        string seed = "<urn:a> <urn:source> '12' . <urn:b> <urn:source> 'bad' . <urn:c> <urn:source> '-2'";
        string expression = recoverErrors ? "COALESCE(xsd:integer(?text),0)" : "xsd:integer(?text)";
        string update = "PREFIX xsd:<" + xsd + "> " + (namedGraph ? "WITH <urn:cast-target> " : "") +
            "DELETE { ?s <urn:source> ?text } INSERT { ?s <urn:number> ?number . ?s <urn:attempted> true } " +
            "WHERE { ?s <urn:source> ?text BIND(" + expression + " AS ?number) " +
            (filterErrors ? "FILTER(BOUND(?number)) " : "") + "}";

        var expected = new List<RdfQuad>();
        foreach (var (subject, number) in new[] { ("urn:a", "12"), ("urn:b", (string?)null), ("urn:c", "-2") })
        {
            if (number is null && filterErrors && !recoverErrors)
            {
                expected.Add(new(new RdfIri(subject), new("urn:source"), new RdfLiteral("bad"), graph));
                continue;
            }
            expected.Add(new(new RdfIri(subject), new("urn:attempted"), new RdfLiteral("true", xsd + "boolean"), graph));
            if (number is not null || recoverErrors)
            {
                expected.Add(new(new RdfIri(subject), new("urn:number"), new RdfLiteral(number ?? "0", xsd + "integer"), graph));
            }
        }
        Guid operation = Guid.NewGuid();
        await using (var database = await OpenAsync(directory))
        {
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
                "INSERT DATA { " + (namedGraph ? "GRAPH <urn:cast-target> { " + seed + " }" : seed) + " }");
            await database.CommitSparqlUpdateAsync(operation, update);
            CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
            await database.CommitSparqlUpdateAsync(operation, update);
            CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
        }
        await using var reopened = await OpenAsync(directory);
        CollectionAssert.AreEquivalent(expected, reopened.Find(new()).ToArray());
    }
}
