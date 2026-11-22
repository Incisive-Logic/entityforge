using EntityForge.Db;
using EntityForge.Db.Storage.Local;
using System.Text.Json;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class JournalManifestCodecTests
{
    private static ChangeJournalManifest Manifest()
    {
        var id = Guid.NewGuid();
        var segments = Enumerable.Range(1, 1024).Select(i => new ChangeJournalSegmentDescriptor(
            i.ToString(), "batch.efcb", "batch.efcb", i, id, "cursor-" + i + "-λ", DateTimeOffset.UnixEpoch,
            100, "checksum", "checksum", i * 100L, 50, i * 100L + 50, 20)).ToArray();
        return new(ChangeJournalManifest.CurrentCodecVersion, 10, id, "old", 0, segments[^1].Cursor, segments);
    }

    [TestMethod]
    public async Task Sync_and_streamed_catalogs_preserve_every_field_and_compress_repetition()
    {
        var manifest = Manifest();
        byte[] expected = JsonSerializer.SerializeToUtf8Bytes(manifest);
        byte[] sync = ChangeJournalManifestCodec.Write(manifest);
        using var stream = new MemoryStream();
        await ChangeJournalManifestCodec.WriteAsync(stream, manifest, default);
        foreach (byte[] encoded in new[] { sync, stream.ToArray() })
        {
            Assert.IsTrue(encoded.Length < expected.Length / 2);
            CollectionAssert.AreEqual(expected, JsonSerializer.SerializeToUtf8Bytes(ChangeJournalManifestCodec.Read(encoded)));
        }
    }

    [TestMethod]
    public void Incomplete_or_damaged_catalog_frame_is_rejected()
    {
        byte[] valid = ChangeJournalManifestCodec.Write(Manifest());
        foreach (int length in new[] { 0, 1, 5, valid.Length / 2, valid.Length - 1 })
        {
            Assert.ThrowsExactly<EntityForgeDbFormatException>(() => ChangeJournalManifestCodec.Read(valid[..length]));
        }

        const int closingMarkerBytes = 6;
        byte[] truncatedBody = valid[..^(closingMarkerBytes + 1)].Concat(valid[^closingMarkerBytes..]).ToArray();
        byte[] extraBody = valid[..^closingMarkerBytes].Concat(new byte[] { 0 }).Concat(valid[^closingMarkerBytes..]).ToArray();
        Assert.ThrowsExactly<EntityForgeDbFormatException>(() => ChangeJournalManifestCodec.Read(truncatedBody));
        Assert.ThrowsExactly<EntityForgeDbFormatException>(() => ChangeJournalManifestCodec.Read(extraBody));
        byte[] invalid = valid.ToArray();
        invalid[0] ^= 1;
        Assert.ThrowsExactly<EntityForgeDbFormatException>(() => ChangeJournalManifestCodec.Read(invalid));
    }

    [TestMethod]
    public async Task Cancelled_stream_does_not_produce_a_complete_catalog()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await ChangeJournalManifestCodec.WriteAsync(stream, Manifest(), cancelled.Token));
        Assert.ThrowsExactly<EntityForgeDbFormatException>(() => ChangeJournalManifestCodec.Read(stream.ToArray()));
    }
}
