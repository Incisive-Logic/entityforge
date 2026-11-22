using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfReadViewTermSharingTests
{
    private readonly TemporaryTestDirectories _directories = new("entityforge-rdf-term-sharing");

    [TestCleanup]
    public void Cleanup() => _directories.Cleanup();

    [TestMethod]
    public async Task Read_views_share_equal_terms_without_merging_distinct_literals_or_mutating_snapshots()
    {
        string directory = _directories.Create();
        await using var pages = await PhysicalPageFile.OpenAsync(Path.Combine(directory, "pages"));
        var physical = await RdfPhysicalAccessPath.OpenAsync(pages, await PhysicalPageSpace.OpenAsync(pages));
        var runtime = new RdfProfileRuntime(physical);
        long version = 0;
        async Task Apply(params RdfMutation[] mutations)
        {
            Guid operation = Guid.NewGuid();
            await runtime.ApplyAsync(new(++version), operation, RdfProfileRuntime.Encode(new(operation, mutations)));
            await runtime.PublishStagedStateAsync(operation);
        }
        RdfQuad Quad(int subject, string lexical = "1") => new(new RdfIri("urn:s:" + subject), new("urn:p"),
            new RdfLiteral(lexical, "http://www.w3.org/2001/XMLSchema#integer"), new RdfIri("urn:g"));
        RdfQuad[] All(RdfProfileRuntime current) => current.FindInMemoryCandidates(null, null, null, null, false).ToArray();
        void Check(RdfProfileRuntime current, int count)
        {
            var quads = All(current);
            Assert.AreEqual(count, quads.Length);
            Assert.AreEqual(1, quads.Select(q => q.Predicate).Distinct(ReferenceEqualityComparer.Instance).Count());
            Assert.AreEqual(1, quads.Select(q => q.Graph).Distinct(ReferenceEqualityComparer.Instance).Count());
            Assert.AreEqual(1, quads.Where(q => ((RdfLiteral)q.Object).LexicalForm == "1")
                .Select(q => q.Object).Distinct(ReferenceEqualityComparer.Instance).Count());
        }
        await Apply(Enumerable.Range(0, 128).Select(i => (RdfMutation)new AssertRdfQuad(Quad(i))).ToArray());
        Check(runtime, 128);
        var snapshot = All(runtime);
        using var lease = runtime.AcquireSnapshot(new(1));
        await Apply(new AssertRdfQuad(Quad(128)), new AssertRdfQuad(Quad(0, "01")), new RetractRdfQuad(Quad(1)));
        Check(runtime, 129);
        var current = All(runtime);
        var sameSubject = current.Where(q => q.Subject == new RdfIri("urn:s:0")).ToArray();
        Assert.AreEqual(2, sameSubject.Length);
        Assert.AreSame(sameSubject[0].Subject, sameSubject[1].Subject);
        Assert.AreNotEqual(sameSubject[0].Object, sameSubject[1].Object, "Equal numeric values retain distinct RDF lexical terms.");
        Assert.AreSame(snapshot[0].Predicate, current[0].Predicate);
        CollectionAssert.AreEquivalent(snapshot, runtime.Find(new(), new DatabaseVersion(1)).ToArray());
        var restored = new RdfProfileRuntime(physical);
        await restored.RestoreCheckpointAsync(new(version), await runtime.CaptureSnapshotPreservingCheckpointAsync());
        Check(restored, 129);
        CollectionAssert.AreEquivalent(current, All(restored));
    }
}
