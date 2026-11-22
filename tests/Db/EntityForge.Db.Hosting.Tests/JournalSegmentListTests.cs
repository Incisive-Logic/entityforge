using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class JournalSegmentListTests
{
    [TestMethod]
    public void Appends_preserve_prior_snapshots_across_block_boundaries()
    {
        var expected = Enumerable.Range(1, 1000).Select(Entry).ToArray();
        var first = JournalSegmentList.From(expected.Take(17));
        var second = first.AddRange(expected.Skip(17).Take(312));
        var third = second.AddRange(expected.Skip(329));
        CollectionAssert.AreEqual(expected.Take(17).ToArray(), first.ToArray());
        CollectionAssert.AreEqual(expected.Take(329).ToArray(), second.ToArray());
        CollectionAssert.AreEqual(expected, third.ToArray());
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i], third[i]);
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = third[-1]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = third[1000]);
        Assert.AreSame(third, third.AddRange([]));
    }

    [TestMethod]
    public void Packed_catalog_preserves_existing_manifest_wire_representation()
    {
        var entries = Enumerable.Range(1, 257).Select(version =>
        {
            var item = Entry(version);
            return version % 2 == 0 ? item : new ChangeJournalSegmentDescriptor(
                $"{item.Version:D20}-{item.TransactionId:N}", item.SegmentFile, item.IndexFile,
                item.Version, item.TransactionId, item.Cursor, item.CreatedUtc, item.Bytes,
                item.SegmentChecksum, item.IndexChecksum, item.RecordOffset, item.RecordLength,
                item.IndexOffset, item.IndexLength);
        }).ToArray();
        var original = new ChangeJournalManifest(ChangeJournalManifest.CurrentCodecVersion,
            4, Guid.NewGuid(), null, null, entries[^1].Cursor, entries);
        var packed = original with
        {
            Segments = JournalSegmentList.From(entries)
        };
        CollectionAssert.AreEqual(ChangeJournalManifestCodec.Write(original), ChangeJournalManifestCodec.Write(packed));
        var recovered = ChangeJournalManifestCodec.Read(ChangeJournalManifestCodec.Write(packed));
        CollectionAssert.AreEqual(entries, recovered.Segments.ToArray());
    }

    private static ChangeJournalSegmentDescriptor Entry(int version) => new(
        version.ToString(), "packed.efcb", "packed.efcb", version, Guid.NewGuid(),
        "cursor-" + version, DateTimeOffset.UnixEpoch.AddSeconds(version), 128,
        "segment-checksum", "index-checksum", version * 128, 64, version * 128 + 64, 64);
}
