using System.Text.Json;
using EntityForge.Db.Durability;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Snapshot_progress_does_not_wake_for_unrelated_preparation(DatabaseProfileId profile)
    {
        var opened = await OpenReservationStore(profile, Path.Combine(_root, "progress-filter"));
        await using var database = opened.Database;
        var kernel = opened.Kernel;
        Guid first = EntityOperationIds.New(), other = EntityOperationIds.New(), seed = EntityOperationIds.New();
        Guid[] subjects = [Guid.NewGuid(), Guid.NewGuid()];
        await kernel.CommitAsync(seed, ReservationPayload(profile, seed, subjects, null, create: true));
        await kernel.PartitionTransactions.PrepareAsync(first, ReservationDescriptor, kernel.CurrentVersion,
            ReservationPayload(profile, first, [subjects[0]], kernel.CurrentVersion));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wait = kernel.PartitionTransactions.WaitForSnapshotProgressAsync([first], timeout.Token).AsTask();
        var field = typeof(EntityForge.Db.Kernel.DatabaseKernel).GetField("_snapshotProgress",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var notification = (TaskCompletionSource)field.GetValue(kernel)!;
        await kernel.PartitionTransactions.PrepareAsync(other, ReservationDescriptor, kernel.CurrentVersion,
            ReservationPayload(profile, other, [subjects[1]], kernel.CurrentVersion));
        var worker = (Task?)typeof(EntityForge.Db.Kernel.DatabaseKernel).GetField("_partitionWorker",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(kernel);
        if (worker is not null)
        {
            await worker.WaitAsync(timeout.Token);
        }

        Assert.IsFalse(notification.Task.IsCompleted, "Unrelated preparation broadcast a progress notification.");
        Assert.IsFalse(wait.IsCompleted);
        await kernel.PartitionTransactions.CompleteAsync(first, ReservationDescriptor, false, timeout.Token);
        await wait.WaitAsync(timeout.Token);
        await kernel.PartitionTransactions.CompleteAsync(other, ReservationDescriptor, false, timeout.Token);
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    public async Task Snapshot_progress_wait_joins_only_selected_preparations_without_blocking_completion(DatabaseProfileId profile, bool commit)
    {
        var opened = await OpenReservationStore(profile, Path.Combine(_root, "progress"));
        await using var database = opened.Database;
        var kernel = opened.Kernel;
        Guid selected = EntityOperationIds.New(), unrelated = EntityOperationIds.New(), seed = EntityOperationIds.New();
        Guid[] subjects = [Guid.NewGuid(), Guid.NewGuid()];
        await kernel.CommitAsync(seed, ReservationPayload(profile, seed, subjects, null, create: true));
        var operations = new[] { selected, unrelated };
        for (int index = 0; index < operations.Length; index++)
        {
            await kernel.PartitionTransactions.PrepareAsync(operations[index], ReservationDescriptor, kernel.CurrentVersion,
                ReservationPayload(profile, operations[index], [subjects[index]], kernel.CurrentVersion));
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = kernel.PartitionTransactions.WaitForSnapshotProgressAsync([selected], timeout.Token).AsTask();
        var second = kernel.PartitionTransactions.WaitForSnapshotProgressAsync([selected], timeout.Token).AsTask();
        Assert.IsFalse(first.IsCompleted);
        Assert.IsFalse(second.IsCompleted);
        await kernel.PartitionTransactions.CompleteAsync(selected, ReservationDescriptor, commit, timeout.Token);
        await Task.WhenAll(first, second).WaitAsync(timeout.Token);
        Assert.AreEqual(DurablePartitionTransactionState.Prepared,
            (await kernel.PartitionTransactions.ReadAsync()).Single(entry => entry.Operation == unrelated).State);
        await kernel.PartitionTransactions.WaitForSnapshotProgressAsync([selected], timeout.Token);
        using var cancelled = new CancellationTokenSource();
        var waiting = kernel.PartitionTransactions.WaitForSnapshotProgressAsync([unrelated], cancelled.Token).AsTask();
        Assert.IsFalse(waiting.IsCompleted);
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
        await kernel.PartitionTransactions.CompleteAsync(unrelated, ReservationDescriptor, false, timeout.Token);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Snapshot_settlement_releases_pin_after_progress_or_cancellation(bool cancel)
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "settlement"),
            DatabaseProfileId.PropertyGraph, authority.Lease, Routing, authority, Options);
        Guid operation = EntityOperationIds.New();
        await node.Authority.ExecuteAsync(async () =>
        {
            await node.Kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, node.Kernel.CurrentVersion,
                NativePayload(DatabaseProfileId.PropertyGraph, Routing, new("a"), operation));
            return true;
        });
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settling = node.ReadCutAsync(cut, new("settle-snapshot", SnapshotPrepared: [operation]), timeout.Token).AsTask();
        Assert.IsFalse(settling.IsCompleted);
        if (cancel)
        {
            timeout.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => settling);
        }
        else
        {
            await node.Authority.ExecuteAsync(async () =>
            {
                await node.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, true);
                return true;
            });
            Assert.IsTrue(JsonSerializer.Deserialize<bool>(await settling.WaitAsync(timeout.Token)));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ReadCutAsync(cut, new("validate-snapshot", SnapshotPrepared: [])).AsTask());
        if (cancel)
        {
            await node.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, false);
        }
    }

    [TestMethod]
    public async Task Snapshot_settlement_lost_response_discharges_all_attempted_pins()
    {
        var (routing, a, b) = SnapshotParticipants();
        Guid operation = Guid.NewGuid();
        a.Evidence = new(new(1), new string('A', 64), [operation], []);
        b.Evidence = new(new(2), new string('B', 64), [], [operation]);
        a.Settling = _ => ValueTask.FromException(new IOException("Settlement response lost"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsExactlyAsync<AggregateException>(() => PartitionRemoteRead.ExecuteAsync<bool>([a, b], routing,
            (_, _) => throw new AssertFailedException("Mixed state was exposed"), timeout.Token, useSnapshots: true).AsTask());
        Assert.AreEqual(2, a.Abandoned);
        Assert.AreEqual(1, b.Abandoned);
        Assert.AreEqual(0, a.Active.Count + b.Active.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Snapshot_settlement_capacity_preserves_ordinary_http_admission(bool closeService)
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "settlement-authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "settlement-http"),
            DatabaseProfileId.PropertyGraph, placement.Lease, Routing, placement, Options, transactionAuthority: decisions);
        using var certificate = PartitionCertificate();
        await using var service = new PartitionParticipantHttpService(node);
        await using var host = await StartPartitionHostAsync(service, certificate);
        using var client = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
        });
        var remote = new HttpPartitionTransactionParticipant(client, PartitionAddress(host), PartitionToken, node.Domain, decisions.Identity);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Guid operation = EntityOperationIds.New();
        await node.Kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, node.Kernel.CurrentVersion,
            NativePayload(DatabaseProfileId.PropertyGraph, Routing, new("a"), operation), timeout.Token);
        var cuts = new List<PartitionReadCut>();
        Task<byte[]>[] waits = [];
        try
        {
            for (int index = 0; index <= PartitionParticipantHttpService.MaximumSnapshotSettlements; index++)
            {
                cuts.Add(await remote.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(25), timeout.Token));
            }

            waits = cuts.Take(PartitionParticipantHttpService.MaximumSnapshotSettlements)
                .Select(cut => remote.ReadCutAsync(cut, new("settle-snapshot", SnapshotPrepared: [operation]), timeout.Token).AsTask()).ToArray();
            var count = typeof(PartitionParticipantHttpService).GetField("_snapshotSettlements",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            while ((int)count.GetValue(service)! != PartitionParticipantHttpService.MaximumSnapshotSettlements)
            {
                await Task.Delay(5, timeout.Token);
            }

            await Assert.ThrowsAsync<HttpRequestException>(() => remote.ReadCutAsync(cuts[^1],
                new("settle-snapshot", SnapshotPrepared: [operation]), timeout.Token).AsTask());
            Assert.IsTrue(JsonSerializer.Deserialize<bool>(await remote.ReadCutAsync(cuts[^1],
                new("validate-snapshot", SnapshotPrepared: []), timeout.Token)));
            if (closeService)
            {
                await service.DisposeAsync().AsTask().WaitAsync(timeout.Token);
                await Assert.ThrowsAsync<HttpRequestException>(() => Task.WhenAll(waits));
            }
            else
            {
                await node.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, false, timeout.Token);
                Assert.IsTrue((await Task.WhenAll(waits).WaitAsync(timeout.Token)).All(bytes => JsonSerializer.Deserialize<bool>(bytes)));
            }
        }
        finally
        {
            await node.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, false);
            foreach (var cut in cuts)
            {
                await node.AbandonCutAsync(cut.Id);
            }

            try
            {
                await Task.WhenAll(waits);
            }
            catch
            {

            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Snapshot_progress_observer_fails_promptly_on_recovery_or_kernel_disposal(bool dispose)
    {
        var opened = await OpenReservationStore(DatabaseProfileId.PropertyGraph, Path.Combine(_root, "progress-close"));
        await using var database = opened.Database;
        var kernel = opened.Kernel;
        Guid operation = EntityOperationIds.New();
        await kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, kernel.CurrentVersion,
            ReservationPayload(DatabaseProfileId.PropertyGraph, operation, [Guid.NewGuid()], null, create: true));
        var wait = kernel.PartitionTransactions.WaitForSnapshotProgressAsync([operation]).AsTask();
        Assert.IsFalse(wait.IsCompleted);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (dispose)
        {
            await kernel.DisposeAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => wait.WaitAsync(timeout.Token));
        }
        else
        {
            kernel.RequireRecovery("qualification_snapshot_wait");
            await Assert.ThrowsExactlyAsync<EntityForgeDbRecoveryRequiredException>(() => wait.WaitAsync(timeout.Token));
        }
    }

}
