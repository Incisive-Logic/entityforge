using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class TimestampSafeReadAdmissionTests
{
    private sealed class Clock : IDatabaseBoundedClock
    {
        public Guid Domain { get; } = Guid.NewGuid();

        internal DatabaseClockInterval? Held;
        internal long Offset;
        internal bool Healthy = true;

        public DatabaseClockInterval Read()
        {
            if (!Healthy)
            {
                throw new InvalidOperationException("Clock health lost.");
            }

            if (Held is { } held)
            {
                return held;
            }

            long now = DateTime.UtcNow.Ticks + Offset;
            return new(now, now);
        }

        internal DurableTimestampState Future()
        {
            long now = DateTime.UtcNow.Ticks;
            Held = new(now, now + TimeSpan.TicksPerSecond / 2);
            return new(Domain, now + TimeSpan.TicksPerSecond / 4, 0, 0, true);
        }
    }

    private sealed class FailReservation : IRedoDurabilityObserver
    {
        internal bool Armed;

        public void Reached(RedoDurabilityStage stage)
        {
            if (Armed && stage == RedoDurabilityStage.AfterWalFlush)
            {
                Armed = false;
                throw new IOException("Lost reservation force acknowledgement.");
            }
        }
    }

    private sealed class AdvanceAfterForce(Clock clock) : IRedoDurabilityObserver
    {
        internal bool Armed;
        internal bool Observed;

        public void Reached(RedoDurabilityStage stage)
        {
            if (!Armed || stage != RedoDurabilityStage.AfterWalFlush)
            {
                return;
            }

            Observed = true;
            clock.Held = null;
            clock.Offset = TimeSpan.FromSeconds(2).Ticks;
        }
    }

    [TestMethod]
    public async Task Timestamp_selection_and_native_force_overlap_uncertainty_but_completion_still_waits()
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-clock-overlap-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock();
        var observer = new AdvanceAfterForce(clock);
        try
        {
            await using var graph = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = clock
            }, observer, null);
            var kernel = ((GraphDatabaseEngine)graph).PartitionKernel;
            clock.Future();
            Guid operation = Guid.NewGuid();
            string descriptor = new('D', 64);
            await kernel.PartitionTransactions.PrepareAsync(operation, descriptor, kernel.CurrentVersion,
                GraphTransactionCodec.Encode(new(operation, [new CreateVertex(GraphVertexId.New(), [])], ReadVersion: kernel.CurrentVersion)));
            var vote = (await kernel.PartitionTransactions.ReadTimestampVoteAsync(operation))!;
            var chosen = await kernel.PartitionTransactions.ChooseTimestampAsync([vote]);
            Assert.IsTrue(clock.Read().EarliestUtcTicks < chosen.Physical);
            observer.Armed = true;
            var receipt = await kernel.PartitionTransactions.CompleteTimestampedAsync(operation, descriptor, true, chosen);
            Assert.IsTrue(observer.Observed, "Durability must be reached before the test advances the clock.");
            Assert.IsNotNull(receipt);
            Assert.IsTrue(clock.Read().EarliestUtcTicks > chosen.Physical);
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
    public async Task Protection_generation_wait_does_not_hold_commit_admission()
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-clock-protection-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock();
        try
        {
            await using var graph = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = clock
            });
            var kernel = ((GraphDatabaseEngine)graph).PartitionKernel;
            clock.Future();
            Task<DatabasePartitionSnapshot> capture = kernel.PartitionTransactions.CaptureSnapshotAsync().AsTask();
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                while (kernel.TimestampProtectionCount == 0)
                {
                    await Task.Delay(1, deadline.Token);
                }

                await kernel.PartitionTransactions.ReadAsync(deadline.Token);
                Assert.IsFalse(capture.IsCompleted);
            }
            finally
            {
                clock.Held = null;
                clock.Offset = TimeSpan.FromSeconds(2).Ticks;
                using var snapshot = await capture;
            }
            Assert.AreEqual(0, kernel.TimestampProtectionCount);
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
    public async Task Future_read_returns_without_clock_progress_and_later_prepare_cannot_backdate()
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-safe-read-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock();
        try
        {
            await using var graph = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = clock
            });
            var kernel = ((GraphDatabaseEngine)graph).PartitionKernel;
            using var protection = await kernel.PartitionTransactions.CaptureSnapshotAsync();
            var time = clock.Future();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            object view = await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(protection, time, null, deadline.Token);
            Assert.IsTrue(clock.Read().EarliestUtcTicks < time.Physical, "Read must complete while time remains uncertain.");
            Assert.IsTrue(kernel.TimestampCostState!.ReservedReadThrough > time.Physical);

            Guid operation = Guid.NewGuid();
            var vertex = GraphVertexId.New();
            string descriptor = new('A', 64);
            await kernel.PartitionTransactions.PrepareAsync(operation, descriptor, kernel.CurrentVersion,
                GraphTransactionCodec.Encode(new(operation, [new CreateVertex(vertex, [])], ReadVersion: kernel.CurrentVersion)));
            var vote = await kernel.PartitionTransactions.ReadTimestampVoteAsync(operation);
            Assert.IsTrue(vote!.CompareTo(time) > 0);
            await kernel.PartitionTransactions.CompleteAsync(operation, descriptor, false);
            using (kernel.PartitionTransactions.EnterTimestampProjection(view))
            {
                Assert.IsNull(await graph.GetVertexAsync(vertex, graph.CurrentVersion));
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task Existing_lower_prepare_still_blocks_absence_until_terminal_application(bool commit)
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-safe-intent-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock();
        try
        {
            await using var graph = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = clock
            });
            var kernel = ((GraphDatabaseEngine)graph).PartitionKernel;
            using var protection = await kernel.PartitionTransactions.CaptureSnapshotAsync();
            Guid operation = Guid.NewGuid();
            var vertex = GraphVertexId.New();
            string descriptor = new('B', 64);
            await kernel.PartitionTransactions.PrepareAsync(operation, descriptor, kernel.CurrentVersion,
                GraphTransactionCodec.Encode(new(operation, [new CreateVertex(vertex, [])], ReadVersion: kernel.CurrentVersion)));
            var time = clock.Future();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task<object> read = kernel.PartitionTransactions.CaptureTimestampProjectionAsync(protection, time, null, deadline.Token).AsTask();
            try
            {
                // Wait for durable admission, not an arbitrary scheduling sleep.
                while (kernel.TimestampCostState!.Physical < time.Physical)
                {
                    await Task.Delay(1, deadline.Token);
                }

                Assert.IsFalse(read.IsCompleted, "An unapplied insert cannot be reported absent.");
                if (commit)
                {
                    var decision = new DurableTimestampState(clock.Domain, time.Physical - 1, 0, 0, true);
                    // Terminal completion retains its clock barrier. Advancing the
                    // test clock permits application of this pre-existing vote.
                    clock.Held = null;
                    clock.Offset = TimeSpan.TicksPerSecond;
                    await kernel.PartitionTransactions.CompleteTimestampedAsync(operation, descriptor, true, decision);
                }
                else
                {
                    await kernel.PartitionTransactions.CompleteAsync(operation, descriptor, false);
                }

                object view = await read;
                using (kernel.PartitionTransactions.EnterTimestampProjection(view))
                {
                    Assert.AreEqual(commit, await graph.GetVertexAsync(vertex, graph.CurrentVersion) is not null);
                }

                if (!commit)
                {
                    Assert.IsTrue(clock.Read().EarliestUtcTicks < time.Physical);
                }
            }
            finally
            {
                deadline.Cancel();
                try
                {
                    await read;
                }
                catch (OperationCanceledException)
                {

                }
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
    public async Task Read_at_reserved_physical_ceiling_forces_a_new_ceiling_for_logical_ticks()
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-safe-ceiling-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock();
        try
        {
            await using var graph = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = clock
            });
            var kernel = ((GraphDatabaseEngine)graph).PartitionKernel;
            using var protection = await kernel.PartitionTransactions.CaptureSnapshotAsync();
            var time = clock.Future();
            long ceiling = kernel.TimestampCostState!.ReservedReadThrough;
            long forces = kernel.TimestampReservationForces;
            time = new(clock.Domain, ceiling, 100, 0, true);
            await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(protection, time, null);
            Assert.AreEqual(forces + 1, kernel.TimestampReservationForces);
            Assert.IsTrue(kernel.TimestampCostState!.ReservedReadThrough > ceiling);
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
    public async Task Invalid_future_or_unhealthy_clock_rejects_before_advancing_admission()
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-safe-bounds-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock();
        try
        {
            await using var graph = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = clock
            });
            var kernel = ((GraphDatabaseEngine)graph).PartitionKernel;
            using var protection = await kernel.PartitionTransactions.CaptureSnapshotAsync();
            var before = kernel.TimestampCostState;
            var time = clock.Future();
            var invalid = new DurableTimestampState(clock.Domain, time.Physical + TimeSpan.TicksPerMinute, 0, 0, true);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(protection, invalid, null));
            clock.Healthy = false;
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(protection, time, null));
            Assert.AreEqual(before, kernel.TimestampCostState);
            clock.Healthy = true;
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task Future_read_promise_survives_reopen_including_uncertain_force(bool failForce)
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-safe-recovery-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock();
        var observer = new FailReservation();
        long readPhysical;
        try
        {
            await using (var graph = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = clock
            }, observer, null))
            {
                var kernel = ((GraphDatabaseEngine)graph).PartitionKernel;
                using var protection = await kernel.PartitionTransactions.CaptureSnapshotAsync();
                var time = clock.Future();
                readPhysical = time.Physical;
                // Force a new reservation rather than reusing the generation's ceiling.
                time = new(clock.Domain, kernel.TimestampCostState!.ReservedReadThrough + TimeSpan.TicksPerMillisecond, 0, 0, true);
                readPhysical = time.Physical;
                observer.Armed = failForce;
                try
                {
                    await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(protection, time, null);
                    Assert.IsFalse(failForce);
                }
                catch (RedoCommitUncertainException) when (failForce)
                {
                    Assert.AreEqual(DatabaseKernelState.RecoveryRequired, kernel.State);
                }
            }
            clock.Held = null;
            clock.Offset = TimeSpan.FromSeconds(2).Ticks;
            await using var reopened = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = clock
            });
            var recovered = ((GraphDatabaseEngine)reopened).PartitionKernel;
            Assert.IsTrue(recovered.TimestampCostState!.ReservedReadThrough > readPhysical);
            Guid operation = Guid.NewGuid();
            string descriptor = new('C', 64);
            await recovered.PartitionTransactions.PrepareAsync(operation, descriptor, recovered.CurrentVersion,
                GraphTransactionCodec.Encode(new(operation, [new CreateVertex(GraphVertexId.New(), [])], ReadVersion: recovered.CurrentVersion)));
            Assert.IsTrue((await recovered.PartitionTransactions.ReadTimestampVoteAsync(operation))!.Physical > readPhysical);
            await recovered.PartitionTransactions.CompleteAsync(operation, descriptor, false);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }
}
