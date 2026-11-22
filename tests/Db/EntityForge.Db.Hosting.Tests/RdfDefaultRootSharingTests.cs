using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfDefaultRootSharingTests
{
    private readonly TemporaryTestDirectories _directories = new("entityforge-rdf-default-roots");

    [TestCleanup]
    public void Cleanup() => _directories.Cleanup();

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Default_root_sharing_survives_named_transitions_snapshots_and_checkpoint_restore(bool blankGraph)
    {
        string directory = _directories.Create();
        await using var pages = await PhysicalPageFile.OpenAsync(Path.Combine(directory, "pages"));
        var physical = await RdfPhysicalAccessPath.OpenAsync(pages, await PhysicalPageSpace.OpenAsync(pages));
        var runtime = new RdfProfileRuntime(physical);
        long version = 0;
        RdfQuad Quad(string name, RdfTerm? graph = null) => new(new RdfIri("urn:" + name), new("urn:p"), new RdfLiteral(name), graph);
        RdfTerm graph = blankGraph ? new RdfBlankNode("named") : new RdfIri("urn:named");
        var d1 = Quad("d1");
        var d2 = Quad("d2");
        var d3 = Quad("d3");
        var d4 = Quad("d4");
        var n1 = Quad("n1", graph);
        var n2 = Quad("n2", graph);
        async Task Apply(params RdfMutation[] mutations)
        {
            Guid operation = Guid.NewGuid();
            await runtime.ApplyAsync(new(++version), operation, RdfProfileRuntime.Encode(new(operation, mutations)));
            await runtime.PublishStagedStateAsync(operation);
        }
        IReadOnlySet<RdfQuad> All(RdfProfileRuntime current) => current.FindInMemoryCandidates(null, null, null, null, false);
        IReadOnlySet<RdfQuad> Default(RdfProfileRuntime current) => current.FindInMemoryCandidates(null, null, null, null, true);
        void Check(RdfProfileRuntime current, RdfQuad[] expected)
        {
            CollectionAssert.AreEquivalent(expected, All(current).ToArray());
            CollectionAssert.AreEquivalent(expected.Where(q => q.Graph is null).ToArray(), Default(current).ToArray());
            if (expected.All(q => q.Graph is null))
            {
                Assert.AreSame(All(current), Default(current), "Identical indexes must retain one immutable root.");
            }
            else
            {
                Assert.AreNotSame(All(current), Default(current));
            }
        }

        Check(runtime, []);
        await Apply(new AssertRdfQuad(d1), new AssertRdfQuad(d2), new AssertRdfQuad(d1));
        Check(runtime, [d1, d2]);
        var original = All(runtime);
        using var lease = runtime.AcquireSnapshot(new(1));

        await Apply(new AssertRdfQuad(d3), new AssertRdfQuad(n1), new RetractRdfQuad(d1),
            new AssertRdfQuad(n2), new AssertRdfQuad(d4));
        Check(runtime, [d2, d3, d4, n1, n2]);
        var mixed = All(runtime);
        var mixedDefault = Default(runtime);
        var restoredMixed = new RdfProfileRuntime(physical);
        await restoredMixed.RestoreCheckpointAsync(new(version), await runtime.CaptureSnapshotPreservingCheckpointAsync());
        Check(restoredMixed, [d2, d3, d4, n1, n2]);

        await Apply(new RetractRdfQuad(n1), new RetractRdfQuad(n2), new RetractRdfQuad(d2));
        Check(runtime, [d3, d4]);
        await Apply(new RetractRdfQuad(n1), new AssertRdfQuad(d3));
        Check(runtime, [d3, d4]);
        await Apply(new RetractRdfQuad(d3), new RetractRdfQuad(d4));
        Check(runtime, []);
        await Apply(new AssertRdfQuad(n1));
        Check(runtime, [n1]);
        await Apply(new RetractRdfQuad(n1), new AssertRdfQuad(d1), new AssertRdfQuad(n2),
            new RetractRdfQuad(n2), new AssertRdfQuad(d2));
        Check(runtime, [d1, d2]);

        CollectionAssert.AreEquivalent(new[] { d1, d2 }, original.ToArray());
        CollectionAssert.AreEquivalent(new[] { d2, d3, d4, n1, n2 }, mixed.ToArray());
        CollectionAssert.AreEquivalent(new[] { d2, d3, d4 }, mixedDefault.ToArray());
        CollectionAssert.AreEquivalent(new[] { d1, d2 }, runtime.Find(new(), new DatabaseVersion(1)).ToArray());
        var restoredDefault = new RdfProfileRuntime(physical);
        await restoredDefault.RestoreCheckpointAsync(new(version), await runtime.CaptureSnapshotPreservingCheckpointAsync());
        Check(restoredDefault, [d1, d2]);
    }
}
