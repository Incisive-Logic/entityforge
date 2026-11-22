using System.Security.Cryptography;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class NativeCorruptionRecoveryTests
{
    private readonly TemporaryTestDirectories _directories = new("entityforge-native-corruption");

    [TestCleanup]
    public void Cleanup() => _directories.Cleanup();

    [TestMethod]
    [DataRow(false, "checkpoint-header")]
    [DataRow(true, "checkpoint-header")]
    [DataRow(false, "checkpoint-missing")]
    [DataRow(true, "checkpoint-missing")]
    [DataRow(false, "checkpoint-stale")]
    [DataRow(true, "checkpoint-stale")]
    [DataRow(false, "wal-header")]
    [DataRow(true, "wal-header")]
    [DataRow(false, "wal-missing")]
    [DataRow(true, "wal-missing")]
    [DataRow(false, "wal-truncated")]
    [DataRow(true, "wal-truncated")]
    [DataRow(false, "wal-missing-before-checkpoint")]
    [DataRow(true, "wal-missing-before-checkpoint")]
    [DataRow(false, "manifest-header")]
    [DataRow(true, "manifest-header")]
    [DataRow(false, "pages-truncated")]
    [DataRow(true, "pages-truncated")]
    public async Task Native_profiles_recover_redundant_images_or_refuse_corruption_without_losing_data(bool rdf, string damage)
    {
        string directory = _directories.Create();
        var vertex = GraphVertexId.New();
        var quad = new RdfQuad(new RdfIri("urn:acknowledged"), new("urn:p"), new RdfLiteral("preserved"), new RdfIri("urn:g"));
        Guid operation = Guid.NewGuid();
        byte[]? staleCheckpoint = null;
        if (rdf)
        {
            await using var database = await RdfDatabase.OpenLocalAsync(new(directory));
            if (damage == "checkpoint-stale")
            {
                await database.CreateCheckpointAsync();
                staleCheckpoint = await File.ReadAllBytesAsync(Path.Combine(directory, LocalRdfDatabaseOptions.DefaultCheckpointFileName));
            }
            await database.CommitAsync(new(operation, [new AssertRdfQuad(quad)]));
            if (!damage.EndsWith("before-checkpoint", StringComparison.Ordinal))
            {
                await database.CreateCheckpointAsync();
            }
        }
        else
        {
            await using var database = await GraphDatabase.OpenLocalAsync(new(directory));
            if (damage == "checkpoint-stale")
            {
                await database.CreateCheckpointAsync();
                staleCheckpoint = await File.ReadAllBytesAsync(Path.Combine(directory, LocalGraphDatabaseOptions.DefaultCheckpointFileName));
            }
            await database.CommitAsync(new(operation, [new CreateVertex(vertex, ["Preserved"]),
                new SetVertexProperty(vertex, "payload", new DatabaseValue.String("preserved"))]));
            if (!damage.EndsWith("before-checkpoint", StringComparison.Ordinal))
            {
                await database.CreateCheckpointAsync();
            }
        }
        string name = damage.Split('-')[0] switch
        {
            "checkpoint" => LocalRdfDatabaseOptions.DefaultCheckpointFileName,
            "wal" => LocalRdfDatabaseOptions.DefaultCommitLogFileName,
            "manifest" => LocalRdfDatabaseOptions.DefaultManifestFileName,
            "pages" => LocalRdfDatabaseOptions.DefaultPageFileName,
            _ => throw new AssertFailedException("Unknown test image.")
        };
        string artifact = Path.Combine(directory, name);
        byte[] original = await File.ReadAllBytesAsync(artifact);
        Assert.IsTrue(original.Length > 0);
        bool missing = damage.Contains("-missing", StringComparison.Ordinal);
        if (missing)
        {
            File.Delete(artifact); // This test's own freshly created database only.
        }
        else if (staleCheckpoint is not null)
        {
            await File.WriteAllBytesAsync(artifact, staleCheckpoint);
        }
        else
        {
            byte[] damaged = damage.EndsWith("truncated", StringComparison.Ordinal) ? [] : (byte[])original.Clone();
            if (damaged.Length != 0)
            {
                damaged[0] ^= 0xff;
            }
            await File.WriteAllBytesAsync(artifact, damaged);
        }
        byte[]? damagedHash = missing ? null : SHA256.HashData(await File.ReadAllBytesAsync(artifact));
        if (damage == "pages-truncated")
        {
            // The logical checkpoint can reconstruct these page images.
            // Successful open is acceptable only when acknowledged data and replay
            // identity survive; an empty replacement database must fail this check.
            await VerifyAcknowledgedData();
            await VerifyAcknowledgedData();
            return;
        }

        var failure = await Assert.ThrowsAsync<Exception>(async () =>
        {
            if (rdf)
            {
                await using var database = await RdfDatabase.OpenLocalAsync(new(directory));
            }
            else
            {
                await using var database = await GraphDatabase.OpenLocalAsync(new(directory));
            }
        });
        Assert.IsTrue(failure is EntityForgeDbException or InvalidDataException,
            "Recovery must reject with a storage/format diagnostic, not an unrelated programming failure: " + failure);
        if (missing)
        {
            Assert.IsFalse(File.Exists(artifact), "Failed open recreated a missing durable artifact.");
        }
        else
        {
            CollectionAssert.AreEqual(damagedHash!, SHA256.HashData(await File.ReadAllBytesAsync(artifact)),
                "Failed open overwrote the corrupt evidence.");
        }
        // Restore exactly the damaged test artifact and verify that failed open
        // did not damage the other files or discard acknowledged data/replay state.
        await File.WriteAllBytesAsync(artifact, original);
        await VerifyAcknowledgedData();

        async Task VerifyAcknowledgedData()
        {
            if (rdf)
            {
                await using var database = await RdfDatabase.OpenLocalAsync(new(directory));
                CollectionAssert.AreEquivalent(new[] { quad }, database.Find(new()).ToArray());
                Assert.AreEqual(1L, (await database.CommitAsync(new(operation, [new AssertRdfQuad(quad)]))).Value);
            }
            else
            {
                await using var database = await GraphDatabase.OpenLocalAsync(new(directory));
                Assert.AreEqual(new DatabaseValue.String("preserved"), database.GetVertex(vertex)!.Properties["payload"]);
                Assert.AreEqual(1L, (await database.CommitAsync(new(operation, [new CreateVertex(vertex, ["Preserved"]),
                    new SetVertexProperty(vertex, "payload", new DatabaseValue.String("preserved"))]))).Value);
            }
        }
    }
}
