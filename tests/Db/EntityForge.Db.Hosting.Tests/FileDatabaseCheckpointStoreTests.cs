using EntityForge.Db.Durability;
using EntityForge.Db.Semantic;
using EntityForge.Db;
using EntityForge.Db.Storage.Local;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class FileDatabaseCheckpointStoreTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.Semantic)]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Checkpoint_publication_phase_measurements_preserve_bytes_and_report_bounded_local_samples(DatabaseProfileId profile)
    {
        var phases = new Dictionary<string, List<double>>();
        var bytes = new List<long>();
        var entries = new List<long>();
        using var telemetry = new DatabaseStorageTelemetry();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == DatabaseStorageTelemetry.MeterName && instrument.Name.StartsWith("entityforge.db.storage.checkpoint."))
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "entityforge.db.storage.checkpoint.phase.duration")
            {
                return;
            }

            string phase = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "entityforge.db.phase")
                {
                    phase = (string)tag.Value!;
                }
            }

            if (!phases.TryGetValue(phase, out var values))
            {
                phases.Add(phase, values = []);
            }

            values.Add(value);
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "entityforge.db.storage.checkpoint.write.bytes")
            {
                bytes.Add(value);
            }

            if (instrument.Name == "entityforge.db.storage.checkpoint.entries")
            {
                entries.Add(value);
            }
        });
        listener.Start();
        const int Samples = 8;
        string path = Path();
        await using var store = new FileDatabaseCheckpointStore(path, telemetry);
        foreach (int size in new[] { 4096, 1600 * 1024 })
        {
            phases.Clear();
            bytes.Clear();
            entries.Clear();
            byte[] content = new byte[size];
            new Random(431).NextBytes(content);
            var checkpoint = new DurableCheckpointEnvelope(Guid.NewGuid(), new(12), new(profile, "phase-proof", 1, content));
            for (int i = 0; i < Samples; i++)
            {
                await store.SaveAsync(checkpoint);
                var restored = await store.LoadAsync();
                CollectionAssert.AreEqual(content, restored!.ProfileState.Content.ToArray());
                Assert.AreEqual(checkpoint.DatabaseIncarnation, restored.DatabaseIncarnation);
            }
            CollectionAssert.AreEquivalent(new[] { "directory_prepare", "serialization", "buffer_flush", "durable_file_flush", "replacement", "directory_sync", "total" }, phases.Keys.ToArray());
            Assert.IsTrue(phases.Values.All(values => values.Count == Samples && values.All(value => value >= 0)));
            Assert.AreEqual(Samples, bytes.Count);
            Assert.IsTrue(bytes.All(value => value == new FileInfo(path).Length));
            Assert.AreEqual(Samples * 2, entries.Count);
            Assert.IsTrue(entries.All(value => value == 0));
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                Profile = profile.ToString(),
                PayloadBytes = size,
                Scope = "Local file checkpoint publication only; not live C-backed Linux or whole-profile maintenance",
                Samples,
                Phases = phases.ToDictionary(pair => pair.Key, pair => new { MeanMs = pair.Value.Average(), MaximumMs = pair.Value.Max() })
            }));
        }
    }

    [TestMethod]
    [DataRow("count")]
    [DataRow("operation")]
    [DataRow("length")]
    public async Task Checksum_valid_coordinator_side_table_rejects_malformed_associations_and_bounds(string corruption)
    {
        string path = Path();
        var coordinator = new DurablePartitionCoordinator("home:test", new('B', 64), new byte[] { 1, 2 });
        var entry = new DurablePartitionTransaction(EntityOperationIds.New(), new('A', 64), new(0), new byte[] { 3 }, DurablePartitionTransactionState.Prepared, coordinator: coordinator);
        var store = new FileDatabaseCheckpointStore(path);
        await store.SaveAsync(new(Guid.NewGuid(), new(0), new(DatabaseProfileId.PropertyGraph, "graph", 1, new byte[] { 1 }),
            versioningMode: DatabaseVersioningMode.PartitionLocal)
        {
            PartitionTransactions = [entry]
        });
        Assert.IsTrue(coordinator.Matches((await store.LoadAsync())!.PartitionTransactions.Single().Coordinator!));
        byte[] bytes = await File.ReadAllBytesAsync(path);
        int extension = bytes.AsSpan().IndexOf("PCH1"u8);
        Assert.IsTrue(extension > 0);
        if (corruption == "count")
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(extension + 4), int.MaxValue);
        }
        else if (corruption == "operation")
        {
            Guid.NewGuid().TryWriteBytes(bytes.AsSpan(extension + 8, 16));
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(extension + 24), int.MaxValue);
        }

        const int headerLength = 9;
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(5, 4));
        SHA256.HashData(bytes.AsSpan(headerLength, payloadLength)).CopyTo(bytes.AsSpan(headerLength + payloadLength, 32));
        await File.WriteAllBytesAsync(path, bytes);
        await Assert.ThrowsExactlyAsync<EntityForgeDbCheckpointException>(() => store.LoadAsync().AsTask());
    }

    private string? _directory;

    [TestCleanup]
    public void Cleanup()
    {
        if (_directory is not null && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [TestMethod]
    public async Task Profile_checkpoint_round_trips_through_atomic_checksummed_envelope()
    {
        string path = Path();
        var expected = new DurableCheckpointEnvelope(Guid.NewGuid(), new(12),
            new(DatabaseProfileId.PropertyGraph, "entityforge.graph.checkpoint", 1, new byte[] { 4, 5 }));
        var store = new FileDatabaseCheckpointStore(path);
        await store.SaveAsync(expected);
        DurableCheckpointEnvelope? actual = await store.LoadAsync();

        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.Version, actual.Version);
        Assert.AreEqual(expected.ProfileState.CodecId, actual.ProfileState.CodecId);
        CollectionAssert.AreEqual(expected.ProfileState.Content.ToArray(), actual.ProfileState.Content.ToArray());
    }

    [TestMethod]
    public async Task Profile_checkpoint_round_trips_content_larger_than_the_bounded_write_chunk()
    {
        string path = Path();
        byte[] content = new byte[192 * 1024];
        Random.Shared.NextBytes(content);
        var expected = new DurableCheckpointEnvelope(Guid.NewGuid(), new(12),
            new(DatabaseProfileId.Semantic, "entityforge.semantic.checkpoint", 1, content));
        var store = new FileDatabaseCheckpointStore(path);

        await store.SaveAsync(expected);
        DurableCheckpointEnvelope? actual = await store.LoadAsync();

        Assert.IsNotNull(actual);
        CollectionAssert.AreEqual(content, actual.ProfileState.Content.ToArray());
    }

    [TestMethod]
    public async Task Corrupt_checkpoint_fails_with_domain_exception()
    {
        string path = Path();
        var store = new FileDatabaseCheckpointStore(path);
        await store.SaveAsync(new(Guid.NewGuid(), new(1), new(DatabaseProfileId.Semantic, "semantic", 1, new byte[] { 1 })));
        byte[] bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= byte.MaxValue;
        await File.WriteAllBytesAsync(path, bytes);
        await Assert.ThrowsExceptionAsync<EntityForgeDbCheckpointException>(async () => await store.LoadAsync());
    }

    [TestMethod]
    public async Task Writer_rejects_duplicate_future_or_non_sha256_transaction_metadata()
    {
        string path = Path();
        var store = new FileDatabaseCheckpointStore(path);
        Guid identity = Guid.NewGuid();
        DurableProfilePayload profile = new(DatabaseProfileId.PropertyGraph, "graph", 1, new byte[] { 1 });
        DurableCommittedTransaction valid = new(identity, new(2), new byte[32],
            DurableCommittedChangeKind.UserDataNoChange, 0);

        await Assert.ThrowsExceptionAsync<EntityForgeDbCheckpointException>(async () =>
            await store.SaveAsync(new(Guid.NewGuid(), new(2), profile, [valid, valid])));
        await Assert.ThrowsExceptionAsync<EntityForgeDbCheckpointException>(async () =>
            await store.SaveAsync(new(Guid.NewGuid(), new(1), profile, [valid])));
        DurableCommittedTransaction shortHash = new(Guid.NewGuid(), new(1), new byte[16],
            DurableCommittedChangeKind.UserDataNoChange, 0);
        await Assert.ThrowsExceptionAsync<EntityForgeDbCheckpointException>(async () =>
            await store.SaveAsync(new(Guid.NewGuid(), new(1), profile, [shortHash])));
    }

    [TestMethod]
    public async Task Checksum_valid_impossible_transaction_count_fails_before_allocation()
    {
        string path = Path();
        var store = new FileDatabaseCheckpointStore(path);
        await store.SaveAsync(new(Guid.NewGuid(), new(1),
            new(DatabaseProfileId.PropertyGraph, "graph", 1, new byte[] { 1 })));
        byte[] bytes = await File.ReadAllBytesAsync(path);
        const int headerLength = 4 + 1 + sizeof(int);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(5, sizeof(int)));
        int cursor = headerLength + DatabaseStorageLayout.EncodedLength + 16 + sizeof(long) + 1;
        int codecLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor, sizeof(int)));
        cursor += sizeof(int) + codecLength + sizeof(int);
        int contentLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor, sizeof(int)));
        cursor += sizeof(int) + contentLength;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(cursor, sizeof(int)), Int32.MaxValue);
        SHA256.HashData(bytes.AsSpan(headerLength, payloadLength))
            .CopyTo(bytes.AsSpan(headerLength + payloadLength, 32));
        await File.WriteAllBytesAsync(path, bytes);

        await Assert.ThrowsExceptionAsync<EntityForgeDbCheckpointException>(async () =>
            await store.LoadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [DataTestMethod]
    [DataRow(DatabaseCheckpointPublicationStage.BeforeReplacement, false)]
    [DataRow(DatabaseCheckpointPublicationStage.AfterReplacement, true)]
    public async Task Replacement_checkpoint_faults_have_reopen_resolvable_outcomes(
        DatabaseCheckpointPublicationStage stage, bool replacementPublished)
    {
        string path = Path();
        var checkpoint = new DurableCheckpointEnvelope(Guid.NewGuid(), new(8),
            new(DatabaseProfileId.Semantic, "semantic", 1, new byte[] { 1, 2 }));
        var observer = new ThrowOnceCheckpointObserver(stage);
        var store = new FileDatabaseCheckpointStore(path, observer: observer);

        await Assert.ThrowsExceptionAsync<EntityForgeDbCheckpointException>(async () =>
            await store.SaveAsync(checkpoint));

        var reopened = new FileDatabaseCheckpointStore(path);
        DurableCheckpointEnvelope? recovered = await reopened.LoadAsync();
        Assert.AreEqual(replacementPublished, recovered is not null);
        if (replacementPublished)
        {
            Assert.AreEqual(checkpoint.Version, recovered!.Version);
        }
    }

    private string Path()
    {
        _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"entityforge-checkpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        return System.IO.Path.Combine(_directory, "kernel.checkpoint");
    }

    private sealed class ThrowOnceCheckpointObserver(DatabaseCheckpointPublicationStage target)
        : IDatabaseCheckpointPublicationObserver
    {
        private int _thrown;

        public void Reached(DatabaseCheckpointPublicationStage stage)
        {
            if (stage == target && Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new IOException("Injected fault.");
            }
        }
    }
}
