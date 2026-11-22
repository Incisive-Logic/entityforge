using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow("success")]
    [DataRow("cancel")]
    [DataRow("failure")]
    public async Task Semantic_deferred_encoding_preserves_post_capture_commits_and_recovery(string outcome)
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        string path = Path.Combine(_root, "semantic-deferred-encoding");
        var node = await PartitionOwnedNode.CreateAsync(path, DatabaseProfileId.Semantic,
            placement.Lease, Routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        try
        {
            var adapter = await node.CreateSemanticExecutionAsync();
            var seed = adapter.CaptureSave(new Item("before", "original complete value"), SemanticEntityWriteOperation.Create());
            await adapter.ExecuteAsync(node.Domain, seed.OperationId, seed.Export());
            await PartitionRemoteRetention.RetainAsync(decisions, [node], Routing, DateTimeOffset.UtcNow.AddMinutes(-1));
            var native = typeof(PartitionOwnedNode).GetField("_native", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(node)!;
            var database = (EntityForge.Db.Semantic.Engine.SemanticDatabaseEngine)native.GetType().GetProperty("Database",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(native)!;
            Assert.AreEqual(0, database.RetainedValidationCommitCount, "Checkpoint must release old conflict metadata.");
            DatabaseKernel.BeforeDeferredCheckpointEncoding.Value = async token =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                if (outcome == "failure")
                {
                    throw new IOException("Injected deferred encoding failure");
                }
            };
            Task retention = PartitionRemoteRetention.RetainAsync(decisions, [node], Routing,
                DateTimeOffset.UtcNow.AddMinutes(-1), token: cancel.Token,
                captured: static () => ValueTask.CompletedTask).AsTask();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var next = adapter.CaptureSave(new Item("during", "complete value during encoding"), SemanticEntityWriteOperation.Create());
                await adapter.ExecuteAsync(node.Domain, next.OperationId, next.Export()).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsFalse(retention.IsCompleted, "Write admission must be released before encoding.");
                if (outcome == "cancel")
                {
                    cancel.Cancel();
                }
            }
            finally
            {
                release.TrySetResult();
                DatabaseKernel.BeforeDeferredCheckpointEncoding.Value = null;
            }
            if (outcome == "success")
            {
                await retention;
                Assert.IsTrue(database.RetainedValidationCommitCount > 0, "Commits after capture must retain their conflict metadata.");
            }
            else
            {
                await Assert.ThrowsAsync<Exception>(() => retention);
            }

            await node.DisposeAsync();
            node = await PartitionOwnedNode.OpenAsync(path, DatabaseProfileId.Semantic,
                placement.Lease, Routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
            var recovered = await node.CreateSemanticExecutionAsync();
            Assert.AreEqual("original complete value", (await recovered.ReadAsync<Item>(node.Domain, "before"))!.Value.Name);
            Assert.AreEqual("complete value during encoding", (await recovered.ReadAsync<Item>(node.Domain, "during"))!.Value.Name);
        }
        finally
        {
            release.TrySetResult();
            DatabaseKernel.BeforeDeferredCheckpointEncoding.Value = null;
            await node.DisposeAsync();
        }
    }
}
