using System.Text.Json;
using EntityForge.Db.Kernel;
using EntityForge.Db.Distributed.Hosting;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(32)]
    [DataRow(544)]
    [DataRow(960)]
    public void Compact_snapshot_summary_is_bounded_and_does_not_discard_replay_evidence(int count)
    {
        var committed = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        var evidence = new DatabasePartitionSnapshotEvidence(new(7), new string('A', 64), [], committed);
        int hash = evidence.GetHashCode();
        var summary = evidence.Summarize();
        Assert.AreSame(summary, evidence.Summarize());
        Assert.AreEqual(hash, evidence.GetHashCode());
        Assert.AreEqual(Math.Min(count, DatabasePartitionSnapshotSummary.MaximumCommitmentSample), summary.CommitmentSample.Count);
        Assert.AreEqual(count, evidence.Committed.Count);
        string wire = JsonSerializer.Serialize(summary);
        Assert.IsTrue(wire.Length < 4096, "Historical commitments must not increase transport beyond the sample bound.");
        var restored = JsonSerializer.Deserialize<DatabasePartitionSnapshotSummary>(wire)!;
        CollectionAssert.AreEqual(summary.CommitmentSample.ToArray(), restored.CommitmentSample.ToArray());
        if (count != 0)
        {
            Guid original = evidence.Committed[0];
            committed[0] = Guid.Empty;
            Assert.AreEqual(original, evidence.Committed[0]);
        }
        Assert.ThrowsExactly<ArgumentException>(() => new DatabasePartitionSnapshotSummary(0, new(7), new string('A', 64), [], []));
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Compact_snapshot_exact_probe_rejects_commitment_outside_sample_and_survives_reopen(DatabaseProfileId profile)
    {
        string path = Path.Combine(_root, "compact-evidence");
        var opened = await OpenReservationStore(profile, path);
        Guid subject = Guid.NewGuid(), omitted;
        await using (opened.Database)
        {
            var kernel = opened.Kernel;
            Guid seed = EntityOperationIds.New();
            await kernel.CommitAsync(seed, ReservationPayload(profile, seed, [subject], null, create: true));
            for (int index = 0; index <= DatabasePartitionSnapshotSummary.MaximumCommitmentSample; index++)
            {
                Guid operation = EntityOperationIds.New();
                await kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, kernel.CurrentVersion,
                    ReservationPayload(profile, operation, [Guid.NewGuid()], null, create: true));
                await kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, true);
            }
            using var snapshot = await kernel.PartitionTransactions.CaptureSnapshotAsync();
            omitted = snapshot.Evidence.Committed.Except(snapshot.Evidence.Summarize().CommitmentSample).Single();
            Assert.IsFalse(await kernel.PartitionTransactions.ValidateSnapshotAsync(snapshot.Evidence, [omitted]));
            Assert.IsTrue(await kernel.PartitionTransactions.ValidateSnapshotAsync(snapshot.Evidence, [Guid.NewGuid()]));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => kernel.PartitionTransactions.ValidateSnapshotAsync(snapshot.Evidence, [Guid.Empty]).AsTask());
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => kernel.PartitionTransactions.ValidateSnapshotAsync(snapshot.Evidence, [omitted, omitted]).AsTask());
        }
        opened = await OpenReservationStore(profile, path);
        await using (opened.Database)
        {
            using var snapshot = await opened.Kernel.PartitionTransactions.CaptureSnapshotAsync();
            Assert.IsTrue(snapshot.Evidence.Committed.Contains(omitted));
            Assert.IsFalse(await opened.Kernel.PartitionTransactions.ValidateSnapshotAsync(snapshot.Evidence, [omitted]));
        }
    }

    [TestMethod]
    public async Task Compact_snapshot_gateway_probes_old_commitments_before_exposing_any_result()
    {
        var (routing, a, b) = SnapshotParticipants();
        Guid old = Guid.NewGuid();
        a.Evidence = new(new(1), new string('A', 64), [old], []);
        b.Evidence = new(new(2), new string('B', 64), [], new[] { old }.Concat(
            Enumerable.Range(0, DatabasePartitionSnapshotSummary.MaximumCommitmentSample).Select(_ => Guid.NewGuid())).ToArray());
        Assert.IsFalse(b.Evidence.Summarize().CommitmentSample.Contains(old));
        int validations = 0;
        b.Reading = (query, _) =>
        {
            Assert.AreEqual("validate-snapshot", query.Kind);
            Assert.IsNotNull(query.SnapshotPrepared);
            bool mixed = query.SnapshotPrepared.Contains(old);
            if (Interlocked.Increment(ref validations) == 1)
            {
                Assert.IsTrue(mixed);
            }

            return ValueTask.FromResult(JsonSerializer.SerializeToUtf8Bytes(!mixed));
        };
        a.Abandoning = () =>
        {
            a.Evidence = new(new(1), new string('C', 64), [], []);
            return ValueTask.CompletedTask;
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool projected = false;
        var result = await PartitionRemoteRead.ExecuteAsync([a, b], routing, (_, _) =>
        {
            Assert.AreEqual(2, validations);
            projected = true;
            return ValueTask.FromResult(true);
        }, timeout.Token, useSnapshots: true);
        Assert.IsTrue(result.Value && projected);
        Assert.AreEqual(1, a.Abandoned);
        Assert.AreEqual(1, b.Abandoned);
        Assert.AreEqual(0, a.Active.Count + b.Active.Count);
    }

    [TestMethod]
    public async Task Compact_snapshot_participant_requires_probe_and_checks_it_before_projection()
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "compact-projection"),
            DatabaseProfileId.PropertyGraph, authority.Lease, Routing, authority, Options);
        Guid operation = EntityOperationIds.New();
        await node.Authority.ExecuteAsync(async () =>
        {
            await node.Kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, node.Kernel.CurrentVersion,
                NativePayload(DatabaseProfileId.PropertyGraph, Routing, new("a"), operation));
            await node.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, true);
            return true;
        });
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadCutAsync(cut, new("validate-snapshot")).AsTask());
            var rejected = PartitionParticipantBinary.Decode<PartitionValidatedRead>(await node.ReadCutAsync(cut,
                new("validate-project", Projection: Batch([new("unsupported")]), SnapshotPrepared: [operation])))!;
            Assert.IsFalse(rejected.Valid);
            Assert.IsNull(rejected.Projection);
            Assert.IsFalse(JsonSerializer.Deserialize<bool>(await node.ReadCutAsync(cut, new("validate-snapshot", SnapshotPrepared: [operation]))));
        }
        finally
        {
            await node.CompleteCutAsync(cut);
        }
    }
}
