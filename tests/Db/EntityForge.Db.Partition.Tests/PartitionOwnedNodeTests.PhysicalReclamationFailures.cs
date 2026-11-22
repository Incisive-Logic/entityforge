using System.Reflection;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Db.Storage;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    [DataRow(DatabaseProfileId.Semantic, false)]
    [DataRow(DatabaseProfileId.Semantic, true)]
    public async Task Reclamation_checkpoint_failure_recovers_latest_values(DatabaseProfileId profile, bool after)
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        string path = Path.Combine(_root, "failure");
        var node = await PartitionOwnedNode.CreateAsync(path, profile, placement.Lease, Routing, placement, Options, transactionAuthority: decisions);
        const int Updates = 8192;
        string expected = "";
        try
        {
            var rdf = profile == DatabaseProfileId.Rdf ? await node.CreateRdfExecutionAsync() : null;
            var semantic = profile == DatabaseProfileId.Semantic ? await node.CreateSemanticExecutionAsync() : null;
            var runtime = (IDatabaseProfileCheckpointMaintenance)typeof(DatabaseKernel)
                .GetField("_profile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node.Kernel)!;
            for (int i = 0; i < Updates && !runtime.RequiresCheckpointMaintenance; i++)
            {
                expected = $"{i}:".PadRight(512, 'x');
                Guid operation = PartitionOwnerOperation.New(Routing, new("a"));
                if (rdf is not null)
                {
                    await rdf.ExecuteAsync(node.Domain, operation, rdf.CaptureSave(new Item("one", expected), operation).Export());
                }
                else
                {
                    await semantic!.ExecuteAsync(node.Domain, operation, semantic.CaptureSave(new Item("one", expected),
                        new SemanticEntityWriteOperation(operation, DateTimeOffset.UtcNow)).Export());
                }
            }
            Assert.IsTrue(runtime.RequiresCheckpointMaintenance);
            var field = typeof(DatabaseKernel).GetField("_checkpointStore", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var failing = new RebindFailureStore((IDatabaseCheckpointStore)field.GetValue(node.Kernel)!, after);
            field.SetValue(node.Kernel, failing);
            bool released = false;
            await Assert.ThrowsAsync<Exception>(() => PartitionRemoteRetention.RetainAsync(decisions, [node], Routing,
                DateTimeOffset.UtcNow.AddMinutes(-1), captured: () =>
                {
                    released = true;
                    return ValueTask.CompletedTask;
                }).AsTask());
            Assert.IsFalse(released, "Traffic must not resume before the replacement checkpoint is durable.");
            Assert.AreEqual(2, failing.Saves, "Failure must occur at the post-maintenance checkpoint.");
        }
        finally
        {
            await node.DisposeAsync();
        }
        node = await PartitionOwnedNode.OpenAsync(path, profile, placement.Lease, Routing, placement, Options, transactionAuthority: decisions);
        try
        {
            string actual = profile == DatabaseProfileId.Rdf
                ? (await (await node.CreateRdfExecutionAsync()).ReadAsync<Item>(node.Domain, "one"))!.Value.Name
                : (await (await node.CreateSemanticExecutionAsync()).ReadAsync<Item>(node.Domain, "one"))!.Value.Name;
            Assert.AreEqual(expected, actual);
        }
        finally
        {
            await node.DisposeAsync();
        }
    }

    private sealed class RebindFailureStore(IDatabaseCheckpointStore inner, bool after) : IDatabaseCheckpointStore, IConcurrentDatabaseCheckpointStore
    {
        public bool SupportsConcurrentPublication => ((IConcurrentDatabaseCheckpointStore)inner).SupportsConcurrentPublication;

        public ValueTask<IDatabaseCheckpointPublication> PreparePublicationAsync(DurableCheckpointEnvelope checkpoint, CancellationToken token = default) =>
            ((IConcurrentDatabaseCheckpointStore)inner).PreparePublicationAsync(checkpoint, token);

        internal int Saves;

        public async ValueTask SaveAsync(DurableCheckpointEnvelope checkpoint, CancellationToken token = default)
        {
            bool fail = ++Saves == 2;
            if (fail && !after)
            {
                throw new IOException("Before maintenance checkpoint replacement");
            }

            await inner.SaveAsync(checkpoint, token);
            if (fail && after)
            {
                throw new IOException("After maintenance checkpoint replacement");
            }
        }

        public ValueTask<DurableCheckpointEnvelope?> LoadAsync(CancellationToken token = default) => inner.LoadAsync(token);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
