using System.Reflection;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    [DataRow(false, true, true)]
    public async Task Native_checkpoint_reopens_during_or_after_incremental_compaction(bool publishReplacement, bool spanCheckpoints, bool advanceBetweenCheckpoints)
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "incremental-authority"));
        string path = Path.Combine(_root, "incremental");
        var node = await PartitionOwnedNode.CreateAsync(path, DatabaseProfileId.Semantic, placement.Lease, Routing, placement, Options, transactionAuthority: decisions);
        const int Count = 128;
        var expected = Enumerable.Range(0, Count).Select(i => $"{i}:".PadRight(spanCheckpoints ? 262144 : 4096, 'x')).ToArray();
        try
        {
            var adapter = await node.CreateSemanticExecutionAsync();
            for (int i = 0; i < Count; i++)
            {
                Guid operation = PartitionOwnerOperation.New(Routing, new("a"));
                await adapter.ExecuteAsync(node.Domain, operation, adapter.CaptureSave(new Item(i.ToString(), expected[i]),
                    new SemanticEntityWriteOperation(operation, DateTimeOffset.UtcNow)).Export());
            }
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            var profile = (IDatabaseProfileRuntime)typeof(DatabaseKernel).GetField("_profile", Private)!.GetValue(node.Kernel)!;
            var log = (IPhysicalDatabaseCommitLog)typeof(DatabaseKernel).GetField("_commitLog", Private)!.GetValue(node.Kernel)!;
            var incremental = (IDatabaseProfileIncrementalCheckpointCompaction)profile;
            // Explicitly begin private work in this single-threaded fixture. The
            // normal production debt threshold remains unchanged; this test targets
            // checkpoint/recovery boundaries rather than threshold policy.
            Assert.IsFalse(await incremental.AdvanceCheckpointCompactionAsync(node.Kernel.CurrentVersion, async (images, token) =>
            {
                var envelope = new DurableTransactionEnvelope(node.Kernel.DatabaseIncarnation, node.Kernel.CurrentVersion, Guid.NewGuid(),
                    new(profile.Profile, profile.CodecId, profile.CodecVersion, ReadOnlyMemory<byte>.Empty),
                    DurableCommittedChangeKind.CheckpointMaintenance, null, node.Kernel.StorageLayout, node.Kernel.VersioningMode);
                await log.CommitWithPhysicalMutationsAsync(envelope, images, token);
            }, default));
            // This ordinary checkpoint includes flushed private pages and may
            // reclaim their redo; it does not publish private replacement roots.
            await node.Kernel.CreateCheckpointAsync();
            expected[0] = "write-between-compaction-slices";
            Guid between = PartitionOwnerOperation.New(Routing, new("a"));
            await adapter.ExecuteAsync(node.Domain, between, adapter.CaptureSave(new Item("0", expected[0]),
                new SemanticEntityWriteOperation(between, DateTimeOffset.UtcNow)).Export());
            if (advanceBetweenCheckpoints)
            {
                // No further checkpoint drives this work. Foreground commits
                // must coexist with private construction, whose roots remain
                // unpublished until the ordinary retention checkpoint below.
                for (int update = 0; update < 8; update++)
                {
                    Guid id = PartitionOwnerOperation.New(Routing, new("a"));
                    expected[1] = $"concurrent-private-build-{update}";
                    await adapter.ExecuteAsync(node.Domain, id, adapter.CaptureSave(new Item("1", expected[1]),
                        new SemanticEntityWriteOperation(id, DateTimeOffset.UtcNow)).Export());
                    await Task.Delay(30);
                }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                while (incremental.CanAdvancePrivateCheckpointCompaction)
                {
                    await Task.Delay(20, timeout.Token);
                }

                Assert.IsTrue(((IDatabaseProfileCheckpointPageReuse)profile).RequiresCheckpointCompaction,
                    "Background construction must leave final catch-up and publication to a checkpoint.");
            }
            if (publishReplacement)
            {
                int slices = 0;
                do
                {
                    await PartitionRemoteRetention.RetainAsync(decisions, [node], Routing, DateTimeOffset.UtcNow.AddMinutes(-1),
                        captured: static () => ValueTask.CompletedTask);
                    Assert.IsTrue(++slices < 64);
                } while (((IDatabaseProfileCheckpointPageReuse)profile).RequiresCheckpointCompaction);
                if (spanCheckpoints && !advanceBetweenCheckpoints)
                {
                    Assert.IsTrue(slices > 1, "Large private work must span native checkpoints.");
                }

                Console.WriteLine($"Native compaction checkpoint slices={slices}; large={spanCheckpoints}");
            }
            for (int i = 0; i < Count; i++)
            {
                Assert.AreEqual(expected[i], (await adapter.ReadAsync<Item>(node.Domain, i.ToString()))!.Value.Name);
            }
        }
        finally
        {
            await node.DisposeAsync();
        }
        node = await PartitionOwnedNode.OpenAsync(path, DatabaseProfileId.Semantic, placement.Lease, Routing, placement, Options, transactionAuthority: decisions);
        try
        {
            var reader = await node.CreateSemanticExecutionAsync();
            for (int i = 0; i < Count; i++)
            {
                Assert.AreEqual(expected[i], (await reader.ReadAsync<Item>(node.Domain, i.ToString()))!.Value.Name);
            }
        }
        finally
        {
            await node.DisposeAsync();
        }
    }
}
