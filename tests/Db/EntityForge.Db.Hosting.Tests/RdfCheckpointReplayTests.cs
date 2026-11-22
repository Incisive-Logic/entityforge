using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfCheckpointReplayTests
{
    private readonly TemporaryTestDirectories _directories = new("entityforge-rdf-replay-checkpoint");

    [TestCleanup]
    public void Cleanup() => _directories.Cleanup();

    [TestMethod]
    public async Task Replay_cannot_claim_a_version_later_than_its_checkpoint_and_failed_restore_preserves_state()
    {
        string directory = _directories.Create();
        await using var pages = await PhysicalPageFile.OpenAsync(Path.Combine(directory, "pages"));
        var physical = await RdfPhysicalAccessPath.OpenAsync(pages, await PhysicalPageSpace.OpenAsync(pages));
        var runtime = new RdfProfileRuntime(physical);
        Guid operation = Guid.NewGuid();
        string hash = new('A', 64);
        var quad = new RdfQuad(new RdfIri("urn:preserved"), new("urn:p"), new RdfLiteral("value"));
        await runtime.ApplyAsync(new(1), operation, RdfProfileRuntime.Encode(new(operation,
            [new AssertRdfQuad(quad)], SourceCommandHash: hash)));
        await runtime.PublishStagedStateAsync(operation);
        var checkpoint = await runtime.CaptureCheckpointAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await runtime.RestoreCheckpointAsync(new(0), checkpoint));
        Assert.AreEqual(new DatabaseVersion(1), runtime.CurrentVersion);
        Assert.AreEqual(quad, runtime.Find(new(Subject: quad.Subject)).Single());
        Assert.IsTrue(runtime.TryGetCommandReplay(operation, hash, out var replay));
        Assert.AreEqual(new DatabaseVersion(1), replay.Version);

        var restored = new RdfProfileRuntime(physical);
        await restored.RestoreCheckpointAsync(new(1), checkpoint);
        Assert.AreEqual(quad, restored.Find(new(Subject: quad.Subject)).Single());
        Assert.IsTrue(restored.TryGetCommandReplay(operation, hash, out replay));
        Assert.AreEqual(new DatabaseVersion(1), replay.Version);
    }
}
