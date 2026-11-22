using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using EntityForge.Db.Durability;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Semantic_frozen_proposal_reconstructs_after_snapshot_loss_and_preparation_aborts(bool live)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var a = new Authority();
        var b = new Authority
        {
            Lease = a.Lease with
            {
                Placement = a.Lease.Placement with
                {
                    Claim = a.Lease.Placement.Claim with
                    {
                        Shard = new("b"),
                        Owner = new("second")
                    }
                }
            }
        };
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"),
            a.Lease.Placement.Claim.Generation.Value, DatabaseProfileId.Semantic, routing, live);
        var authority = lifetime.Authority;
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.Semantic, a.Lease, routing, a, Options, transactionAuthority: authority, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.Semantic, b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
        IPartitionTransactionParticipant[] owners = [first, second];
        var client = new PartitionApplicationClient(authority, routing, owners, ReadClock);
        using var http = new HttpClient();
        var store = new HttpSemanticEntityClient(http, first.Domain.Generation).EntityStore<Item>();
        var selected = store.CaptureSave(new("one", "stale"), SemanticEntityWriteOperation.Create());
        var evidence = await PartitionSemanticPlanning.SelectAsync(first.Domain.Generation, selected.OperationId, routing, owners, [selected.Export()], default);
        await authority.BindApplicationAsync(evidence);
        var expected = await first.PlanApplicationAsync(selected.OperationId);
        var winner = store.CaptureSave(new("one", "winner"), SemanticEntityWriteOperation.Create());
        await client.DeliverAsync(winner.OperationId, winner.Export());
        var reconstructed = await first.PlanApplicationAsync(selected.OperationId);
        Assert.AreEqual(expected.ReadVersion, reconstructed.ReadVersion);
        CollectionAssert.AreEqual(expected.Payload.ToArray(), reconstructed.Payload.ToArray());
        await Assert.ThrowsExactlyAsync<EntityOperationAbortedException>(() => client.DeliverAsync(selected.OperationId, selected.Export()).AsTask());
        Assert.IsFalse((await authority.ReadDecisionAsync(selected.OperationId))!.Commit);
        Assert.IsTrue((await first.Kernel.PartitionTransactions.ReadAsync()).All(p => p.State != DurablePartitionTransactionState.Prepared));
        Assert.IsTrue((await second.Kernel.PartitionTransactions.ReadAsync()).All(p => p.State != DurablePartitionTransactionState.Prepared));
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    public async Task Query_updates_reject_stale_cuts_terminalise_and_replay_after_reopen(DatabaseProfileId profile, bool live)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var a = new Authority();
        var b = new Authority
        {
            Lease = a.Lease with
            {
                Placement = a.Lease.Placement with
                {
                    Claim = a.Lease.Placement.Claim with
                    {
                        Shard = new("b"),
                        Owner = new("second")
                    }
                }
            }
        };
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"),
            a.Lease.Placement.Claim.Generation.Value, profile, routing, live);
        var authority = lifetime.Authority;
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a, Options, transactionAuthority: authority, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
        IPartitionTransactionParticipant[] owners = [first, second];
        var client = new PartitionApplicationClient(authority, routing, owners, ReadClock);
        foreach (var owner in new[] { first, second })
        {
            var id = EntityOperationIds.New();
            await owner.Kernel.CommitWithReceiptAsync(id, NativePayload(profile, routing, owner.Domain.Partition, id));
        }
        string statement = profile == DatabaseProfileId.PropertyGraph ? "MATCH (n) SET n.updated = true"
            : "INSERT { ?s <urn:query:updated> \"yes\" } WHERE { ?s ?p ?o }";
        PartitionQueryWrite Request(Guid id) => new(first.Domain.Generation, profile, id,
            profile == DatabaseProfileId.PropertyGraph ? "gql" : "sparql", statement);
        async Task<PartitionApplicationBinding> Capture(Guid operation)
        {
            var captured = await PartitionRemoteRead.ExecuteAsync(new IPartitionReadParticipant[] { first, second }, routing,
                async (read, token) => new PartitionQueryUpdate.Evidence(read.Position,
                    [new(first.Domain.Identity, await read.ReadAsync("a", new("query-relation"), token)),
                        new(second.Domain.Identity, await read.ReadAsync("b", new("query-relation"), token))]), default);
            return new(operation, [first.Domain.Identity, second.Domain.Identity], [PartitionQueryUpdate.Encode(Request(operation))],
                planningEvidence: JsonSerializer.SerializeToUtf8Bytes(await PartitionQueryUpdate.MaterializeAsync(Request(operation), captured.Value, routing, default), PartitionParticipantWire.Json));
        }
        var stale = EntityOperationIds.New();
        var selected = await Capture(stale);
        await authority.BindApplicationAsync(selected);
        var intervening = EntityOperationIds.New();
        await first.Kernel.CommitWithReceiptAsync(intervening, NativePayload(profile, routing, first.Domain.Partition, intervening));
        await Assert.ThrowsExactlyAsync<EntityOperationAbortedException>(() => client.UpdateQueryAsync(Request(stale), default).AsTask());
        Assert.IsFalse((await authority.ReadDecisionAsync(stale))!.Commit);
        await Assert.ThrowsExactlyAsync<EntityOperationAbortedException>(() => client.UpdateQueryAsync(Request(stale), default).AsTask());
        Assert.IsTrue((await first.Kernel.PartitionTransactions.ReadAsync()).All(p => p.State != DurablePartitionTransactionState.Prepared));
        Assert.IsTrue((await second.Kernel.PartitionTransactions.ReadAsync()).All(p => p.State != DurablePartitionTransactionState.Prepared));

        var cancelled = EntityOperationIds.New();
        var application = await Capture(cancelled);
        await authority.BindApplicationAsync(application);
        await authority.AbortApplicationAsync(cancelled, application.Descriptor);
        await Assert.ThrowsExactlyAsync<EntityOperationAbortedException>(() => client.UpdateQueryAsync(Request(cancelled), default).AsTask());
        Assert.IsNull(await authority.ReadPlanAsync(cancelled));

        var committed = EntityOperationIds.New();
        var receipt = await client.UpdateQueryAsync(Request(committed), default);
        await first.DisposeAsync();
        await second.DisposeAsync();
        await using var reopenedA = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a, Options, transactionAuthority: authority, timestampClock: ReadClock);
        await using var reopenedB = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
        var recovered = new PartitionApplicationClient(authority, routing, [reopenedA, reopenedB], ReadClock);
        var replay = await recovered.UpdateQueryAsync(Request(committed), default);
        Assert.IsTrue(receipt.Position.Covers(replay.Position) && replay.Position.Covers(receipt.Position));
        await Assert.ThrowsExactlyAsync<EntityOperationAbortedException>(() => recovered.UpdateQueryAsync(Request(stale), default).AsTask());
    }
}
