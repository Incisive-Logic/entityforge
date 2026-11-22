using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_uri_encoding_limit_rejects_sequence_writes_across_restart(bool coalesce)
    {
        string directory = CreateDirectory();
        Guid operation = Guid.NewGuid();
        var input = new RdfQuad(new RdfIri("urn:input"), new("urn:text"), new RdfLiteral(new string(' ', 350_000)));
        var seed = new RdfQuad(new RdfIri("urn:seed"), new("urn:p"), new RdfLiteral("original"));
        string expression = coalesce ? "COALESCE(ENCODE_FOR_URI(?text),'fallback')" : "ENCODE_FOR_URI(?text)";
        string update = "DELETE DATA { <urn:seed> <urn:p> 'original' }; INSERT DATA { <urn:marker> <urn:p> 1 }; " +
            "INSERT { <urn:encoded> <urn:p> ?encoded } WHERE { <urn:input> <urn:text> ?text BIND(" + expression + " AS ?encoded) }; " +
            "INSERT DATA { <urn:tail> <urn:p> 2 }";
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await OpenAsync(directory);
            if (restart == 0)
            {
                await database.CommitAsync(new(Guid.NewGuid(), new RdfMutation[] { new AssertRdfQuad(input), new AssertRdfQuad(seed) }));
            }
            for (int retry = 0; retry < 2; retry++)
            {
                await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(() => database.CommitSparqlUpdateAsync(operation, update).AsTask());
                CollectionAssert.AreEquivalent(new[] { input, seed }, database.Find(new()).ToArray());
            }
        }
    }

    [TestMethod]
    [DataRow("a", "a", 1)]
    [DataRow("%", "%25", 3)]
    [DataRow("é", "%C3%A9", 6)]
    [DataRow("😀", "%F0%9F%98%80", 12)]
    public void Sparql_uri_encoding_checks_expanded_size_before_allocating_result(string atom, string encoded, int width)
    {
        const int maximum = 1_048_576;
        string text = String.Concat(Enumerable.Repeat(atom, maximum / width)) + new string('a', maximum % width);
        RdfQuad[] Data(string value) => [new(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral(value, language: "en"))];
        const string query = "SELECT (ENCODE_FOR_URI(?v) AS ?result) { <urn:s> <urn:p> ?v }";
        var row = ((RdfSparqlSelectResult)RdfSparql.ExecuteRelation(Data(text), query, default)).Bindings.Single();
        var result = (RdfLiteral)row["result"];
        Assert.AreEqual(maximum, result.LexicalForm.Length);
        StringAssert.StartsWith(result.LexicalForm, encoded);
        Assert.IsNull(result.Language);
        Assert.AreEqual("http://www.w3.org/2001/XMLSchema#string", result.Datatype);
        foreach (string expression in new[] { "ENCODE_FOR_URI(?v)", "COALESCE(ENCODE_FOR_URI(?v),'fallback')" })
        {
            Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.ExecuteRelation(Data(text + "a"),
                "SELECT (" + expression + " AS ?result) { <urn:s> <urn:p> ?v }", default));
        }
    }
}
