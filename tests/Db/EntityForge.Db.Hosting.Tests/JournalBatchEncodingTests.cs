using EntityForge.Db;
using EntityForge.Db.Kernel;
using EntityForge.Db.Storage.Local;
using System.IO.Compression;
using System.Text;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class JournalBatchEncodingTests
{
    private static CommittedChangeSet Change(Guid incarnation, long version, byte[] payload,
        DatabaseProfileId profile = DatabaseProfileId.PropertyGraph) => new(incarnation, new(version), Guid.NewGuid(), profile,
        new DatabaseChangeCursorCodec().Encode(incarnation, new(version)), new("test.codec", 1, payload, 1,
            new([new("subject", "Unicode-\u03bb-\uD83D\uDE00")], ["overflow"])));

    [TestMethod]
    public async Task Large_batch_preserves_order_offsets_and_replay_after_failed_encoding()
    {
        const int Count = 257;
        string path = Path.Combine(Path.GetTempPath(), "ef-parallel-journal", Guid.NewGuid().ToString("N"));
        Guid id = Guid.NewGuid();
        var changes = Enumerable.Range(1, Count).Select(version => Change(id, version,
            Enumerable.Repeat((byte)version, version == Count ? 131079 : 97).ToArray())).ToArray();
        var invalid = changes.ToArray();
        var last = invalid[^1];
        invalid[^1] = new(last.DatabaseIncarnation, last.Version, last.TransactionId, last.Profile,
            new(new string('x', 16 * 1024 + 1)), last.Payload);
        try
        {
            await using (var store = new SegmentedDatabaseCommittedChangeStore(path))
            {
                await Assert.ThrowsExceptionAsync<EntityForgeDbStorageException>(async () => await store.AppendBatchAsync(invalid));
                Assert.IsNull((await store.GetBoundsAsync()).Current);
                await store.AppendBatchAsync(changes);
                await store.PublishThroughAsync(changes[^1].Cursor);
            }
            await using var reopened = new SegmentedDatabaseCommittedChangeStore(path);
            await reopened.AppendBatchAsync(changes);
            await reopened.NotifyPublishedAsync(changes[^1].Cursor);
            var page = await reopened.ReadPageAfterAsync(null, new(), Count);
            Assert.AreEqual(Count, page.Changes.Count);
            for (int index = 0; index < Count; index++)
            {
                Assert.AreEqual(changes[index].Version, page.Changes[index].Version);
                Assert.AreEqual(changes[index].TransactionId, page.Changes[index].TransactionId);
                CollectionAssert.AreEqual(changes[index].Payload.Content.ToArray(), page.Changes[index].Payload.Content.ToArray());
            }
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [TestMethod]
    public async Task Pooled_sections_match_the_wire_body_and_round_trip_across_buffer_boundaries()
    {
        using var encoder = new ChangeJournalSectionEncoder();
        foreach (var profile in Enum.GetValues<DatabaseProfileId>())
        {
            foreach (int size in new[] { 1, 65_537, 131_079, 3 })
            {
                byte[] payload = new byte[size];
                new Random(123).NextBytes(payload);
                var original = Change(Guid.NewGuid(), 9, payload, profile);
                byte[] record = encoder.Record(original, default);
                using var wire = new MemoryStream(record);
                using var header = new BinaryReader(wire, Encoding.UTF8, leaveOpen: true);
                Assert.AreEqual(ChangeJournalBinaryCodec.RecordVersion, header.ReadInt32());
                using var decompressed = new BrotliStream(wire, CompressionMode.Decompress);
                using var body = new BinaryReader(decompressed, Encoding.UTF8, leaveOpen: true);
                CollectionAssert.AreEqual(original.DatabaseIncarnation.ToByteArray(), body.ReadBytes(16));
                Assert.AreEqual(9L, body.ReadInt64());
                CollectionAssert.AreEqual(original.TransactionId.ToByteArray(), body.ReadBytes(16));
                Assert.AreEqual((byte)profile, body.ReadByte());
                Assert.AreEqual(original.Cursor.Value, ReadString(body));
                Assert.AreEqual("test.codec", ReadString(body));
                Assert.AreEqual(1, body.ReadInt32());
                Assert.AreEqual(size, body.ReadInt32());
                CollectionAssert.AreEqual(payload, body.ReadBytes(size));
                using var input = new MemoryStream(record);
                var restored = await ChangeJournalBinaryCodec.ReadChangeSetAsync(input, record.Length, default);
                Assert.AreEqual(original.Cursor, restored.Cursor);
                Assert.AreEqual(original.Payload.EffectCount, restored.Payload.EffectCount);
                Assert.AreEqual(original.Payload.Kind, restored.Payload.Kind);
                Assert.AreEqual(original.Payload.CodecId, restored.Payload.CodecId);
                Assert.AreEqual(original.Payload.CodecVersion, restored.Payload.CodecVersion);
                CollectionAssert.AreEqual(payload, restored.Payload.Content.ToArray());
                CollectionAssert.AreEqual(original.Payload.Routing.Keys.ToArray(), restored.Payload.Routing.Keys.ToArray());
                byte[] index = encoder.Index(profile, original.Payload.Routing, default);
                using var indexInput = new MemoryStream(index);
                var routing = await ChangeJournalBinaryCodec.ReadIndexAsync(indexInput, index.Length, default);
                Assert.AreEqual(profile, routing.Profile);
                CollectionAssert.AreEqual(new[] { "overflow" }, routing.OverflowedKinds.ToArray());
            }
        }
    }

    [TestMethod]
    public void Cancellation_and_invalid_strings_do_not_poison_the_reused_encoder()
    {
        using var encoder = new ChangeJournalSectionEncoder();
        var value = Change(Guid.NewGuid(), 1, [1]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => encoder.Record(value, cancellation.Token));
        var invalid = new CommittedChangeSet(value.DatabaseIncarnation, value.Version, value.TransactionId,
            value.Profile, new(new string('x', 16 * 1024 + 1)), value.Payload);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => encoder.Record(invalid, default));
        Assert.IsTrue(encoder.Record(value, default).Length > 4);
    }

    [TestMethod]
    public async Task Combined_manifest_preserves_predecessor_across_reopen_and_exact_batch_retry()
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-journal-batch", Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        var first = Change(id, 1, [1]);
        var second = Change(id, 2, [2]);
        try
        {
            await using (var store = new SegmentedDatabaseCommittedChangeStore(path))
            {
                await store.AppendBatchAsync([first, second], publishedThrough: first.Cursor);
                // Simulate loss of the response before notification and current publication.
            }
            await using var reopened = new SegmentedDatabaseCommittedChangeStore(path);
            await reopened.NotifyPublishedAsync(first.Cursor);
            Assert.AreEqual(first.Cursor, (await reopened.GetBoundsAsync()).Current);
            var page = await reopened.ReadPageAfterAsync(null, new(), 10);
            Assert.AreEqual(1, page.Changes.Count);
            await reopened.AppendBatchAsync([first, second], publishedThrough: first.Cursor);
            Assert.AreEqual(first.Cursor, (await reopened.GetBoundsAsync()).Current);
            await reopened.PublishThroughAsync(second.Cursor);
            await reopened.NotifyPublishedAsync(second.Cursor);
            // A late old predecessor must never move the durable frontier backwards.
            await reopened.AppendBatchAsync([first, second], publishedThrough: first.Cursor);
            Assert.AreEqual(second.Cursor, (await reopened.GetStatusAsync()).PublishedCurrent);
            Assert.AreEqual(2, (await reopened.ReadPageAfterAsync(null, new(), 10)).Changes.Count);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [TestMethod]
    public async Task Threshold_flush_keeps_current_change_hidden_until_publication()
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-journal-threshold", Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        var first = Change(id, 1, [1]);
        var second = Change(id, 2, [2]);
        try
        {
            await using (var store = new WalBackedDatabaseCommittedChangeStore(path, maximumPendingChanges: 2))
            {
                await store.AppendAsync(first);
                await store.PublishThroughAsync(first.Cursor);
                await store.NotifyPublishedAsync(first.Cursor);
                await store.AppendAsync(second); // Triggers the batch flush, but not current publication.
                Assert.AreEqual(first.Cursor, (await store.GetBoundsAsync()).Current);
                Assert.AreEqual(1, (await store.ReadPageAfterAsync(null, new(), 10)).Changes.Count);
            }
            await using var reopened = new SegmentedDatabaseCommittedChangeStore(path);
            await reopened.NotifyPublishedAsync(first.Cursor);
            Assert.AreEqual(1, (await reopened.ReadPageAfterAsync(null, new(), 10)).Changes.Count);
            await reopened.PublishThroughAsync(second.Cursor);
            await reopened.NotifyPublishedAsync(second.Cursor);
            Assert.AreEqual(2, (await reopened.ReadPageAfterAsync(null, new(), 10)).Changes.Count);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [TestMethod]
    public async Task Invalid_later_section_cannot_publish_the_batch_or_predecessor()
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-journal-invalid", Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        var first = Change(id, 1, [1]);
        var second = Change(id, 2, [2]);
        var invalid = new CommittedChangeSet(id, second.Version, second.TransactionId, second.Profile,
            new(new string('x', 16 * 1024 + 1)), second.Payload);
        try
        {
            await using var store = new SegmentedDatabaseCommittedChangeStore(path);
            await Assert.ThrowsExactlyAsync<EntityForgeDbStorageException>(async () =>
                await store.AppendBatchAsync([first, invalid], publishedThrough: first.Cursor));
            Assert.AreEqual(0, (await store.GetStatusAsync()).SegmentCount);
            Assert.IsNull((await store.GetStatusAsync()).PublishedCurrent);
            await store.AppendBatchAsync([first, second], publishedThrough: first.Cursor);
            await store.NotifyPublishedAsync(first.Cursor);
            Assert.AreEqual(1, (await store.ReadPageAfterAsync(null, new(), 10)).Changes.Count);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [TestMethod]
    public async Task Replay_classification_preserves_exact_identity_and_version_gaps_after_reopen()
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-journal-replay-search", Guid.NewGuid().ToString("N"));
        Guid id = Guid.NewGuid();
        var changes = new[] { Change(id, 2, [2]), Change(id, 4, [4]), Change(id, 8, [8]) };
        try
        {
            await using (var store = new SegmentedDatabaseCommittedChangeStore(path))
            {
                await store.AppendBatchAsync(changes);
            }

            await using var reopened = new SegmentedDatabaseCommittedChangeStore(path);
            foreach (var change in changes)
            {
                Assert.AreEqual(ChangeReplayClassification.AlreadyMaterialized,
                    await reopened.ClassifyReplayAsync(change, default));
                await Assert.ThrowsExactlyAsync<EntityForgeDbFormatException>(async () =>
                    await reopened.ClassifyReplayAsync(Change(id, change.Version.Value, [9]), default));
            }
            foreach (long version in new long[] { 1, 3, 5, 9 })
            {
                Assert.AreEqual(ChangeReplayClassification.RequiresMaterialization,
                    await reopened.ClassifyReplayAsync(Change(id, version, [1]), default));
            }
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    private static string ReadString(BinaryReader reader) => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
}
