using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("ex:g\\q")]
    [DataRow("ex:g\\;")]
    [DataRow("ex:g\\#")]
    [DataRow("ex:g\\'")]
    public async Task Sparql_update_malformed_escaped_name_sequences_leave_no_partial_commit(string graph)
    {
        string directory = CreateDirectory();
        RdfQuad[] expected = [new(new RdfIri("urn:existing"), new RdfIri("urn:p"), new RdfLiteral("kept"))];
        await using (var database = await OpenAsync(directory))
        {
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:existing> <urn:p> 'kept' }");
            var version = database.CurrentVersion;
            // The last three names contain legal escaped punctuation, but
            // deliberately omit COPY's required destination. Their punctuation
            // must not hide the malformed tail or let the first INSERT commit.
            string tail = graph.EndsWith("q", StringComparison.Ordinal) ? " TO DEFAULT" : "";
            string update = "PREFIX ex:<urn:example:> INSERT DATA { ex:unexpected ex:p 1 }; COPY GRAPH " + graph + tail;
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(), update).AsTask());
            Assert.AreEqual(version, database.CurrentVersion);
            CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
        }
        await using var reopened = await OpenAsync(directory);
        CollectionAssert.AreEquivalent(expected, reopened.Find(new()).ToArray());
    }

    [TestMethod]
    [DataRow("#")]
    [DataRow(";")]
    [DataRow("'")]
    [DataRow("(")]
    [DataRow("$")]
    [DataRow("_")]
    [DataRow("~")]
    [DataRow(".")]
    [DataRow("-")]
    [DataRow("!")]
    [DataRow("&")]
    [DataRow(")")]
    [DataRow("*")]
    [DataRow("+")]
    [DataRow(",")]
    [DataRow("=")]
    [DataRow("/")]
    [DataRow("?")]
    [DataRow("@")]
    [DataRow("%20")]
    public async Task Sparql_update_escaped_local_names_preserve_sequence_boundaries_and_replay(string escaped)
    {
        string directory = CreateDirectory();
        string name = "ex:g\\" + escaped + "tail";
        string update = "PREFIX ex: <urn:example:> INSERT DATA { GRAPH " + name + " { ex:s ex:p 1 } }; " +
            "COPY GRAPH " + name + " TO DEFAULT; INSERT DATA { ex:after ex:p 2 }";
        var one = new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer");
        RdfQuad[] expected =
        [
            new(new RdfIri("urn:example:s"), new RdfIri("urn:example:p"), one, new RdfIri("urn:example:g" + escaped + "tail")),
            new(new RdfIri("urn:example:s"), new RdfIri("urn:example:p"), one),
            new(new RdfIri("urn:example:after"), new RdfIri("urn:example:p"), new RdfLiteral("2", "http://www.w3.org/2001/XMLSchema#integer"))
        ];
        var operation = Guid.NewGuid();
        await using (var database = await OpenAsync(directory))
        {
            var receipt = await database.CommitSparqlUpdateAsync(operation, update);
            CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
            Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(operation, update));
        }
        await using var reopened = await OpenAsync(directory);
        CollectionAssert.AreEquivalent(expected, reopened.Find(new()).ToArray());
    }
}
