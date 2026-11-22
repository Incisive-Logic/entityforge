using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Durability;
using EntityForge.Db.Kernel;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Root_cohort_preserves_latest_admission_when_clock_upper_bound_decreases()
    {
        var (routing, a, b) = SnapshotParticipants();
        a.ClockDomain = b.ClockDomain = ReadClock.Domain;
        await using var epochs = new PartitionReadEpoch([a, b], routing, ReadClock);
        await epochs.ExecuteAsync(async (read, ct) =>
        {
            var observed = read.Timestamp!;
            var admitted = new DurableTimestampState(observed.Domain, observed.Physical + 10000, 0, 0, true);
            var later = new DurableTimestampState(observed.Domain, admitted.Physical + 10000, 0, 0, true);
            var column = new DatabaseRootIndexColumn("test", "key");
            var selection = new PartitionQueryExchangeRequest("gql", "MATCH (n) RETURN n");
            PartitionRootIndexDemand Demand(DurableTimestampState time) => new(Guid.NewGuid(), column, "value", selection)
            {
                AdmissionTime = time
            };
            var cohort = read.ForRootCohort([Demand(admitted), Demand(later), Demand(observed)]);
            Assert.AreEqual(later, cohort.Timestamp);
            Assert.AreEqual(observed, read.Timestamp, "Selecting a cohort must not mutate another reader.");
            a.Reading = (query, _) =>
            {
                Assert.AreEqual(later, query.ReadTime, "Owners must execute at the selected time, not relabel an older result.");
                return ValueTask.FromResult("17"u8.ToArray());
            };
            await cohort.ReadAsync("a", new("vertex", Guid.NewGuid()), ct);
            Assert.ThrowsExactly<InvalidDataException>(() => read.ForRootCohort([
                Demand(new(Guid.NewGuid(), later.Physical, 0, 0, true))]));
            Assert.ThrowsExactly<InvalidDataException>(() => read.ForRootCohort([
                Demand(new(later.Domain, later.Physical, 0, 0, false))]));
            return true;
        }, null, default);
    }

    [TestMethod]
    public async Task Failed_epoch_acquisition_does_not_permanently_poison_later_reads()
    {
        var (routing, a, b) = SnapshotParticipants();
        a.ClockDomain = b.ClockDomain = ReadClock.Domain;
        int acquisitions = 0;
        a.Acquiring = _ => Interlocked.Increment(ref acquisitions) == 1
            ? ValueTask.FromException(new InvalidDataException("Injected acquisition rejection")) : ValueTask.CompletedTask;
        await using (var epochs = new PartitionReadEpoch([a, b], routing, ReadClock))
        {
            bool recovered = false;
            int failures = 0;
            for (int attempt = 0; attempt < 5 && !recovered; attempt++)
            {
                try
                {
                    recovered = (await epochs.ExecuteAsync((read, ct) => ValueTask.FromResult(true), null, default)).Value;
                }
                catch (AggregateException error)
                {
                    Assert.IsTrue(error.Flatten().InnerExceptions.All(e => e is InvalidDataException));
                    failures++;
                }
            }
            Assert.IsTrue(failures > 0, "The original failure must remain visible.");
            Assert.IsTrue(recovered, "A reported acquisition failure must not remain cached forever.");
            Assert.AreEqual(2, acquisitions);
        }
        Assert.AreEqual(0, a.Active.Count + b.Active.Count);
    }

    [TestMethod]
    public async Task History_acquisition_reserves_clock_correction_margin()
    {
        var (routing, a, b) = SnapshotParticipants();
        a.ClockDomain = b.ClockDomain = ReadClock.Domain;
        await PartitionRemoteRead.ExecuteProtectedAsync([a, b], routing, (read, ct) =>
        {
            // Even a receiver 500 ms behind the sender must admit this deadline
            // without weakening its strict 30-second maximum protection lifetime.
            var receiverNow = DateTimeOffset.UtcNow.AddMilliseconds(-500);
            Assert.IsTrue(a.HistoryExpires - receiverNow <= PartitionReadCuts.MaximumDuration);
            Assert.AreEqual(a.HistoryExpires, b.HistoryExpires);
            Assert.IsTrue(a.HistoryExpires > receiverNow);
            return ValueTask.FromResult(true);
        }, null);
        Assert.AreEqual(0, a.Active.Count + b.Active.Count);
    }
}
