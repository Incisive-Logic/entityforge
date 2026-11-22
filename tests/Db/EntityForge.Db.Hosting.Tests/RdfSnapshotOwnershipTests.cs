using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfSnapshotOwnershipTests
{
    private readonly TemporaryTestDirectories _directories = new("entityforge-rdf-snapshot-owner");

    [TestCleanup]
    public void Cleanup() => _directories.Cleanup();

    [TestMethod]
    [DataRow("public")]
    [DataRow("bounded")]
    [DataRow("unordered")]
    [DataRow("estimate")]
    [DataRow("candidates")]
    [DataRow("predicate-order")]
    [DataRow("pattern-order")]
    [DataRow("count")]
    [DataRow("group-count")]
    [DataRow("graph-names")]
    [DataRow("prefix")]
    public async Task Rdf_read_requires_an_active_lease_from_the_same_database(string reader)
    {
        await using var first = await RdfDatabase.OpenLocalAsync(new(_directories.Create()));
        await using var second = await RdfDatabase.OpenLocalAsync(new(_directories.Create()));
        var subject = new RdfIri("urn:owner:subject");
        var predicate = new RdfIri("urn:owner:predicate");
        var firstQuad = new RdfQuad(subject, predicate, new RdfLiteral("first"));
        var secondQuad = new RdfQuad(subject, predicate, new RdfLiteral("second"));
        await first.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(firstQuad)]));
        await second.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(secondQuad)]));
        using var foreign = first.AcquireSnapshot();
        using var own = second.AcquireSnapshot();
        Assert.AreEqual(foreign.Version, own.Version, "The regression needs matching version numbers in distinct databases.");
        var engine = (RdfDatabaseEngine)second;
        object Read(RdfSnapshotLease lease) => reader switch
        {
            "public" => engine.Find(new(), lease),
            "bounded" => ((IRdfBoundedSnapshotReader)engine).FindBounded(new(), lease, 10, default),
            "unordered" => engine.FindUnordered(new(), lease),
            "estimate" => engine.EstimateCandidates(new(), lease),
            "candidates" => engine.FindInMemoryCandidates(subject, predicate, null, null, true, lease),
            "predicate-order" => engine.FindInMemoryByPredicateOrderedSubject(predicate, false, lease),
            "pattern-order" => engine.FindInMemoryByPatternOrderedSubject(new(), false, lease),
            "count" => engine.CountInMemory(predicate, secondQuad.Object, null, lease),
            "group-count" => engine.CountDefaultGraphByObject(predicate, lease),
            "graph-names" => engine.ReadSparqlGraphNames(lease, 10, default),
            "prefix" => engine.ReadSparqlPrefix(new(), lease, 10, false, default),
            _ => throw new ArgumentOutOfRangeException(nameof(reader))
        };
        Assert.ThrowsExactly<ArgumentNullException>(() => Read(null!));
        var failure = Assert.ThrowsExactly<ArgumentException>(() => Read(foreign));
        Assert.AreEqual("snapshot", failure.ParamName);
        _ = Read(own);
        CollectionAssert.AreEqual(new[] { firstQuad }, first.Find(new(), foreign).ToArray());
        CollectionAssert.AreEqual(new[] { secondQuad }, second.Find(new(), own).ToArray());
        own.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => Read(own));
    }
}
