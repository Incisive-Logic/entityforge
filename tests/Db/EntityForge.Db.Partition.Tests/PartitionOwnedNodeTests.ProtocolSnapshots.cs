using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Rdf;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Prepared_evidence_checkpoint_with_snapshot_recovers_without_a_vote_becoming_a_commit()
    {
        string path = Path.Combine(_root, "prepared-snapshot");
        Guid operation = EntityOperationIds.New();
        const string descriptor = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        byte[] payload = NativePayload(DatabaseProfileId.PropertyGraph, Routing, new("a"), operation);
        await using (var database = await GraphDatabase.OpenLocalAsync(new(path)
        {
            VersioningMode = DatabaseVersioningMode.PartitionLocal
        }))
        {
            using var snapshot = await database.AcquireSnapshotAsync();
            var kernel = ((GraphDatabaseEngine)database).PartitionKernel;
            await kernel.PartitionTransactions.PrepareAsync(operation, descriptor, new(0), payload);
        }
        await using var reopened = await GraphDatabase.OpenLocalAsync(new(path)
        {
            VersioningMode = DatabaseVersioningMode.PartitionLocal
        });
        var protocol = ((GraphDatabaseEngine)reopened).PartitionKernel.PartitionTransactions;
        var selected = (await protocol.ReadAsync()).Single();
        Assert.AreEqual(EntityForge.Db.Durability.DurablePartitionTransactionState.Prepared, selected.State);
        CollectionAssert.AreEqual(payload, selected.Payload.ToArray());
        Assert.AreEqual(0L, reopened.CurrentVersion.Value);
        await protocol.CompleteAsync(operation, descriptor, false);
        Assert.IsNull(await protocol.ResolveAsync(operation, descriptor));
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    public async Task Protocol_checkpoints_preserve_active_snapshots_and_recover_terminal_evidence(DatabaseProfileId profile, bool commit)
    {
        string path = Path.Combine(_root, "snapshot-protocol");
        Guid operation = EntityOperationIds.New();
        const string descriptor = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        DatabaseKernel kernel;
        IAsyncDisposable database;
        IDisposable snapshot;
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            var graph = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal
            });
            database = graph;
            kernel = ((GraphDatabaseEngine)graph).PartitionKernel;
            snapshot = await graph.AcquireSnapshotAsync();
        }
        else
        {
            var rdf = await RdfDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal
            });
            database = rdf;
            kernel = ((RdfDatabaseEngine)rdf).PartitionKernel;
            snapshot = await rdf.AcquireSnapshotAsync();
        }
        await using (database)
        {
            using (snapshot)
            {
                var payload = NativePayload(profile, Routing, new("a"), operation);
                await kernel.PartitionTransactions.PrepareAsync(operation, descriptor, new(0), payload);
                var receipt = await kernel.PartitionTransactions.CompleteAsync(operation, descriptor, commit);
                Assert.AreEqual(commit, receipt is not null);
                Assert.AreEqual(DatabaseKernelState.Ready, kernel.State);
                if (database is IGraphDatabase graph)
                {
                    graph.ValidateSnapshot((GraphSnapshotLease)snapshot);
                    var vertex = ((CreateVertex)GraphTransactionCodec.Decode(operation, payload.AsSpan()).Mutations[0]).Vertex;
                    Assert.IsNull(await graph.GetVertexAsync(vertex, new(0)));
                    if (commit)
                    {
                        Assert.IsNotNull(await graph.GetVertexAsync(vertex, graph.CurrentVersion));
                        await Assert.ThrowsExactlyAsync<GraphWriteConflictException>(() => graph.CommitAsync(
                            new(Guid.NewGuid(), [], new(0), [new VertexRead(vertex)])).AsTask());
                        // Preserved history is usable, not merely retained bytes: an unrelated old read remains admissible.
                        await graph.CommitAsync(new(Guid.NewGuid(), [], new(0), [new VertexRead(new(Guid.NewGuid()))]));
                    }
                    await Assert.ThrowsExactlyAsync<GraphSnapshotLeaseException>(() => graph.CreateCheckpointAsync().AsTask());
                }
            }
            // Reopen directly from the protocol checkpoint, without a maintenance checkpoint.
        }
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            await using var reopened = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal
            });
            kernel = ((GraphDatabaseEngine)reopened).PartitionKernel;
            Assert.AreEqual(commit, (await kernel.PartitionTransactions.ResolveAsync(operation, descriptor)) is not null);
            await kernel.CreateCheckpointAsync(); // ordinary maintenance still works after drainage
        }
        else
        {
            await using var reopened = await RdfDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal
            });
            kernel = ((RdfDatabaseEngine)reopened).PartitionKernel;
            Assert.AreEqual(commit, (await kernel.PartitionTransactions.ResolveAsync(operation, descriptor)) is not null);
            await kernel.CreateCheckpointAsync(); // ordinary maintenance still works after drainage
        }
    }
}
