using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("b", false)]
    [DataRow("b", true)]
    [DataRow("é", false)]
    [DataRow("é", true)]
    [DataRow("a.b", false)]
    public async Task Sparql_update_rejects_blank_label_reuse_between_where_clauses(string label, bool named)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var seed = new RdfQuad(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("one"),
            named ? new RdfIri("urn:g") : null);
        await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(seed)]));
        var before = database.CurrentVersion;
        string pattern = "_:" + label + " <urn:p> ?v";
        if (named)
        {
            pattern = "GRAPH <urn:g> { " + pattern + " }";
        }
        string update = "INSERT { <urn:first> <urn:copy> ?v } WHERE { " + pattern + " }; " +
            "INSERT { <urn:second> <urn:copy> ?v } # WHERE is still a keyword after this comment\nWHERE { " + pattern + " }";
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            database.CommitSparqlUpdateAsync(Guid.NewGuid(), update).AsTask());
        Assert.AreEqual(before, database.CurrentVersion);
        CollectionAssert.AreEquivalent(new[] { seed }, database.Find(new()).ToArray());
    }

    [TestMethod]
    [DataRow("a", "b")]
    [DataRow("é", "ê")]
    [DataRow("𐐀", "á")]
    [DataRow("_", "0")]
    public async Task Sparql_update_keeps_template_label_reuse_and_distinct_where_labels_legal(string firstLabel, string secondLabel)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var seed = new RdfQuad(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("one"));
        await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(seed)]));
        string update = "INSERT { _:shared <urn:first> ?v } WHERE { _:" + firstLabel + " <urn:p> ?v }; " +
            "INSERT { _:shared <urn:second> ?v } WHERE { _:" + secondLabel + " <urn:p> ?v . FILTER('_:a WHERE' = '_:a WHERE') }";
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), update);
        var quads = database.Find(new()).ToArray();
        Assert.HasCount(3, quads);
        Assert.IsTrue(quads.Contains(seed));
        var first = quads.Single(q => q.Predicate == new RdfIri("urn:first"));
        var second = quads.Single(q => q.Predicate == new RdfIri("urn:second"));
        Assert.AreEqual(new RdfLiteral("one"), first.Object);
        Assert.AreEqual(first.Object, second.Object);
        Assert.IsInstanceOfType<RdfBlankNode>(first.Subject);
        Assert.IsInstanceOfType<RdfBlankNode>(second.Subject);
        Assert.AreNotEqual(first.Subject, second.Subject);
    }
}
