using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("shared")]
    [DataRow("sparql-autos1")]
    public async Task Sparql_data_commands_keep_cross_graph_blank_identity_and_fresh_request_scope(string label)
    {
        string directory = CreateDirectory();
        string insert = "INSERT DATA { _:" + label + " <urn:self> _:" + label + " ; <urn:marker> 'explicit' . " +
            "GRAPH <urn:g> { _:" + label + " <urn:self> _:" + label + " } [] <urn:marker> 'anonymous' }";
        RdfQuad[] committed;
        Guid operation = Guid.NewGuid();
        await using (var database = await OpenAsync(directory))
        {
            await database.CommitSparqlUpdateAsync(operation, insert);
            RdfQuad[] first = database.Find(new()).ToArray();
            Assert.HasCount(4, first);
            Assert.HasCount(2, first.Select(q => q.Subject).Distinct().ToArray());
            RdfQuad[] self = first.Where(q => q.Predicate == new RdfIri("urn:self")).ToArray();
            Assert.HasCount(2, self);
            Assert.AreEqual(self[0].Subject, self[1].Subject);
            Assert.IsTrue(self.All(q => q.Subject == q.Object));
            Assert.HasCount(1, self.Where(q => q.Graph is null).ToArray());
            Assert.HasCount(1, self.Where(q => q.Graph == new RdfIri("urn:g")).ToArray());
            await database.CommitSparqlUpdateAsync(operation, insert);
            CollectionAssert.AreEquivalent(first, database.Find(new()).ToArray());
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), insert);
            committed = database.Find(new()).ToArray();
            Assert.HasCount(8, committed);
            Assert.HasCount(4, committed.Select(q => q.Subject).Distinct().ToArray());
        }
        await using var reopened = await OpenAsync(directory);
        await reopened.CommitSparqlUpdateAsync(operation, insert);
        CollectionAssert.AreEquivalent(committed, reopened.Find(new()).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_data_commands_preserve_exact_terms_and_replay_without_redeleting(bool named)
    {
        const string xsd = "http://www.w3.org/2001/XMLSchema#";
        const string objects = "\"01\"^^xsd:integer, \"1\"^^xsd:integer, \"1.0\"^^xsd:decimal, \"1\", \"1\"^^xsd:string, \"text\"@EN, \"text\"@en, \"text\"@fr, <urn:object>";
        string insert = "PREFIX xsd:<" + xsd + "> INSERT DATA { <urn:s> <urn:p> " + objects + " . GRAPH <urn:g> { <urn:s> <urn:p> " + objects + " } }";
        RdfTerm[] terms = [new RdfLiteral("01", xsd + "integer"), new RdfLiteral("1", xsd + "integer"),
            new RdfLiteral("1.0", xsd + "decimal"), new RdfLiteral("1"), new RdfLiteral("text", language: "en"),
            new RdfLiteral("text", language: "fr"), new RdfIri("urn:object")];
        RdfQuad[] expected = terms.SelectMany(term => new[]
        {
            new RdfQuad(new RdfIri("urn:s"), new RdfIri("urn:p"), term),
            new RdfQuad(new RdfIri("urn:s"), new RdfIri("urn:p"), term, new RdfIri("urn:g"))
        }).ToArray();
        var deleted = new RdfQuad(new RdfIri("urn:s"), new RdfIri("urn:p"), new RdfLiteral("1", xsd + "integer"), named ? new RdfIri("urn:g") : null);
        string body = "<urn:s> <urn:p> 1 . <urn:absent> <urn:p> 1";
        string delete = "DELETE DATA { " + (named ? "GRAPH <urn:g> { " + body + " }" : body) + " }";
        string directory = CreateDirectory();
        Guid operation = Guid.NewGuid();
        RdfSparqlUpdateCommit receipt;
        await using (var database = await OpenAsync(directory))
        {
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), insert);
            CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
            receipt = await database.CommitSparqlUpdateAsync(operation, delete);
            CollectionAssert.AreEquivalent(expected.Where(q => q != deleted).ToArray(), database.Find(new()).ToArray());
            await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(deleted)]));
            Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(operation, delete));
            CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
        }
        await using var reopened = await OpenAsync(directory);
        Assert.AreEqual(receipt, await reopened.CommitSparqlUpdateAsync(operation, delete));
        CollectionAssert.AreEquivalent(expected, reopened.Find(new()).ToArray());
    }

    [TestMethod]
    [DataRow("INSERT DATA { ?s <urn:p> <urn:o> }")]
    [DataRow("INSERT DATA { GRAPH ?g { <urn:s> <urn:p> <urn:o> } }")]
    [DataRow("DELETE DATA { _:b <urn:p> <urn:o> }")]
    [DataRow("DELETE DATA { [] <urn:p> <urn:o> }")]
    [DataRow("DELETE DATA { <urn:s> <urn:p> [ <urn:q> <urn:o> ] }")]
    [DataRow("DELETE DATA { <urn:s> <urn:p> ?o }")]
    public async Task Sparql_data_commands_reject_non_ground_forms_before_sequence_commit(string invalid)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var sentinel = new RdfQuad(new RdfIri("urn:keep"), new RdfIri("urn:p"), new RdfLiteral("unchanged"));
        await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(sentinel)]));
        var version = database.CurrentVersion;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "INSERT DATA { <urn:new> <urn:p> 1 }; " + invalid).AsTask());
        Assert.AreEqual(version, database.CurrentVersion);
        CollectionAssert.AreEquivalent(new[] { sentinel }, database.Find(new()).ToArray());
    }
}
