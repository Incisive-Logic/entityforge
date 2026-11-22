using System.Reflection;
using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    private sealed class PausedMaintenanceReclamation(bool fail) : IRedoDurabilityObserver
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();

        public void Reached(RedoDurabilityStage stage)
        {
            if (stage != RedoDurabilityStage.BeforeReplacementInitialization)
            {
                return;
            }

            Started.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(20)))
            {
                throw new TimeoutException("Reclamation pause");
            }

            if (fail)
            {
                throw new IOException("Reclamation failed after resumed foreground commit");
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Maintained_checkpoint_resumes_before_reclamation_and_recovers_foreground_tail(bool fail)
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        string path = Path.Combine(_root, "detached-maintenance");
        var node = await PartitionOwnedNode.CreateAsync(path, DatabaseProfileId.Semantic,
            placement.Lease, Routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        var pause = new PausedMaintenanceReclamation(fail);
        try
        {
            var adapter = await node.CreateSemanticExecutionAsync();
            var profile = typeof(DatabaseKernel).GetField("_profile", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel)!;
            for (int i = 0; i < 8192 && !((IDatabaseProfileCheckpointMaintenance)profile).RequiresCheckpointMaintenance; i++)
            {
                var request = adapter.CaptureSave(new Item("one", $"{i}:".PadRight(512, 'x')), SemanticEntityWriteOperation.Create());
                await adapter.ExecuteAsync(node.Domain, request.OperationId, request.Export());
            }
            Assert.IsTrue(((IDatabaseProfileCheckpointMaintenance)profile).RequiresCheckpointMaintenance);
            var log = typeof(DatabaseKernel).GetField("_commitLog", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel)!;
            typeof(GroupedRedoDatabaseCommitLog).GetField("_observer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(log, pause);
            var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task retention = PartitionRemoteRetention.RetainAsync(decisions, [node], Routing, DateTimeOffset.UtcNow.AddMinutes(-1),
                captured: () =>
                {
                    captured.TrySetResult();
                    return ValueTask.CompletedTask;
                }).AsTask();
            try
            {
                await pause.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var update = adapter.CaptureSave(new Item("one", "post-maintenance durable value"), SemanticEntityWriteOperation.Create());
                await adapter.ExecuteAsync(node.Domain, update.OperationId, update.Export()).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsFalse(retention.IsCompleted);
            }
            finally
            {
                pause.Release.Set();
            }
            if (fail)
            {
                await Assert.ThrowsAsync<Exception>(() => retention);
            }
            else
            {
                await retention;
            }
        }
        finally
        {
            pause.Release.Set();
            await node.DisposeAsync();
            pause.Release.Dispose();
        }
        node = await PartitionOwnedNode.OpenAsync(path, DatabaseProfileId.Semantic,
            placement.Lease, Routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        try
        {
            var recovered = await node.CreateSemanticExecutionAsync();
            Assert.AreEqual("post-maintenance durable value", (await recovered.ReadAsync<Item>(node.Domain, "one"))!.Value.Name);
        }
        finally
        {
            await node.DisposeAsync();
        }
    }
}
