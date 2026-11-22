using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Repeated_partition_updates_reclaim_physical_space_and_reopen(DatabaseProfileId profile)
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        string path = Path.Combine(_root, "space");
        var node = await PartitionOwnedNode.CreateAsync(path, profile, placement.Lease, Routing, placement, Options, transactionAuthority: decisions);
        var sizes = new List<long>();
        long firstHighWater = 0;
        const int Epochs = 4, UpdatesPerEpoch = 2048;
        string expected = "";
        try
        {
            Func<Item, Guid, byte[]> capture;
            Func<Guid, byte[], ValueTask<PartitionCommitResult>> deliver;
            Func<ValueTask<string>> read;
            if (profile == DatabaseProfileId.PropertyGraph)
            {
                var adapter = await node.CreateGraphExecutionAsync();
                capture = (item, id) => adapter.CaptureSave(item, id).Export();
                deliver = (id, bytes) => adapter.ExecuteAsync(node.Domain, id, bytes);
                read = async () => (await adapter.ReadAsync<Item>(node.Domain, "one"))!.Value.Name;
            }
            else if (profile == DatabaseProfileId.Rdf)
            {
                var adapter = await node.CreateRdfExecutionAsync();
                capture = (item, id) => adapter.CaptureSave(item, id).Export();
                deliver = (id, bytes) => adapter.ExecuteAsync(node.Domain, id, bytes);
                read = async () => (await adapter.ReadAsync<Item>(node.Domain, "one"))!.Value.Name;
            }
            else
            {
                var adapter = await node.CreateSemanticExecutionAsync();
                capture = (item, id) => adapter.CaptureSave(item, new SemanticEntityWriteOperation(id, DateTimeOffset.UtcNow)).Export();
                deliver = (id, bytes) => adapter.ExecuteAsync(node.Domain, id, bytes);
                read = async () => (await adapter.ReadAsync<Item>(node.Domain, "one"))!.Value.Name;
            }

            for (int epoch = 0; epoch < Epochs; epoch++)
            {
                for (int update = 0; update < UpdatesPerEpoch; update++)
                {
                    expected = $"{epoch}:{update}:".PadRight(512, 'x');
                    Guid operation = PartitionOwnerOperation.New(Routing, new("a"));
                    await deliver(operation, capture(new("one", expected), operation));
                }
                string pages = Directory.GetFiles(path, "entityforge.pages", SearchOption.AllDirectories).Single();
                long before = new FileInfo(pages).Length;
                await PartitionRemoteRetention.RetainAsync(decisions, [node], Routing, DateTimeOffset.UtcNow.AddMinutes(-1),
                    captured: static () => ValueTask.CompletedTask);
                long after = new FileInfo(pages).Length;
                sizes.Add(after);
                if (epoch == 0)
                {
                    firstHighWater = before;
                }

                Assert.AreEqual(expected, await read());
                Console.WriteLine($"{profile} epoch={epoch} before={before} after={after}");
            }
            // The replacement checkpoint must also recover later ordinary writes
            // that consume its authorized free-page pool through physical redo.
            expected = "post-checkpoint-reused-page";
            Guid tail = PartitionOwnerOperation.New(Routing, new("a"));
            await deliver(tail, capture(new("one", expected), tail));
            Assert.AreEqual(expected, await read());
            const long MaximumAdditionalBytes = 64L * 1024 * 1024;
            Assert.IsTrue(sizes.Skip(1).All(size => size <= firstHighWater + MaximumAdditionalBytes),
                $"Repeated updates did not reuse checkpoint-covered space: {string.Join(",", sizes)}");
        }
        finally
        {
            await node.DisposeAsync();
        }
        node = await PartitionOwnedNode.OpenAsync(path, profile, placement.Lease, Routing, placement, Options, transactionAuthority: decisions);
        try
        {
            string actual = profile switch
            {
                DatabaseProfileId.PropertyGraph => (await (await node.CreateGraphExecutionAsync()).ReadAsync<Item>(node.Domain, "one"))!.Value.Name,
                DatabaseProfileId.Rdf => (await (await node.CreateRdfExecutionAsync()).ReadAsync<Item>(node.Domain, "one"))!.Value.Name,
                _ => (await (await node.CreateSemanticExecutionAsync()).ReadAsync<Item>(node.Domain, "one"))!.Value.Name
            };
            Assert.AreEqual(expected, actual);
        }
        finally
        {
            await node.DisposeAsync();
        }
    }
}
