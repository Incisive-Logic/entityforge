using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Graph;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;
using EntityForge.Db.Semantic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [EntityType("partition-node-tests", "item")]
    public sealed record MappingChangedItem(string Id, string Name, string Extra);

    private static byte[] MappingCapture(DatabaseProfileId profile, Guid generation, bool changed = false)
    {
        using var http = new HttpClient();
        if (profile == DatabaseProfileId.Semantic)
        {
            var client = new HttpSemanticEntityClient(http, generation);
            return changed
                ? client.EntityStore<MappingChangedItem>().CaptureSave(new("item", "value", "extra"), SemanticEntityWriteOperation.Create()).Export()
                : client.EntityStore<Item>().CaptureSave(new("item", "value"), SemanticEntityWriteOperation.Create()).Export();
        }
        var graph = new HttpGraphEntityClient(http, generation);
        return changed
            ? graph.EntityStore<MappingChangedItem>().CaptureSave(new("item", "value", "extra"), new GraphEntityWriteOptions(EntityOperationIds.New())).Captured.Export()
            : graph.EntityStore<Item>().CaptureSave(new("item", "value"), new GraphEntityWriteOptions(EntityOperationIds.New())).Captured.Export();
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Established_mapping_validation_does_not_allocate_timestamp_and_rejects_changed_manifest(DatabaseProfileId profile)
    {
        var authority = new Authority();
        using var store = new FileTimestampStore(Path.Combine(_root, "clock"), "mapping-domain");
        var clock = new DatabaseSharedTimestampService(store);
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "database"), profile,
            authority.Lease, Routing, authority, Options, timestamps: clock);
        var request = MappingCapture(profile, node.Domain.Generation);
        var initial = (await clock.ObserveAsync()).State;
        await node.InitializeApplicationMappingAsync(request);
        var registered = (await clock.ObserveAsync()).State;
        Assert.AreEqual(initial.Allocated + 1, registered.Allocated);
        var version = node.Kernel.CurrentVersion;
        int validations = authority.Validations;
        await node.InitializeApplicationMappingAsync(MappingCapture(profile, node.Domain.Generation));
        var checkedAgain = (await clock.ObserveAsync()).State;
        Assert.AreEqual(registered.Allocated, checkedAgain.Allocated);
        Assert.AreEqual(registered.Closed, checkedAgain.Closed);
        Assert.AreEqual(version, node.Kernel.CurrentVersion);
        Assert.IsTrue(authority.Validations > validations);
        await Assert.ThrowsExactlyAsync<EntityMappingMismatchException>(async () =>
            await node.InitializeApplicationMappingAsync(MappingCapture(profile, node.Domain.Generation, changed: true)));
        Assert.AreEqual(registered.Allocated, (await clock.ObserveAsync()).State.Allocated);
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Reopened_mapping_is_checked_freshly_and_revoked_owner_cannot_validate_without_timestamp(DatabaseProfileId profile)
    {
        var authority = new Authority();
        string path = Path.Combine(_root, "database");
        using var store = new FileTimestampStore(Path.Combine(_root, "clock"), "mapping-domain");
        var clock = new DatabaseSharedTimestampService(store);
        byte[] capture;
        await using (var node = await PartitionOwnedNode.CreateAsync(path, profile, authority.Lease, Routing, authority, Options, timestamps: clock))
        {
            capture = MappingCapture(profile, node.Domain.Generation);
            await node.InitializeApplicationMappingAsync(capture);
        }
        long allocated = (await clock.ObserveAsync()).State.Allocated;
        await using var reopened = await PartitionOwnedNode.OpenAsync(path, profile, authority.Lease, Routing, authority, Options, timestamps: clock);
        await reopened.InitializeApplicationMappingAsync(capture);
        Assert.AreEqual(allocated, (await clock.ObserveAsync()).State.Allocated);
        var lease = authority.Lease;
        authority.Lease = lease with
        {
            Placement = lease.Placement with
            {
                Claim = lease.Placement.Claim with
                {
                    FencingToken = new(lease.Placement.Claim.FencingToken.Value + 1)
                }
            }
        };
        try
        {
            await Assert.ThrowsAsync<EntityForge.Db.Distributed.Sharding.StaleShardPlacementException>(async () => await reopened.InitializeApplicationMappingAsync(capture));
        }
        finally
        {
            authority.Lease = lease;
        }
        Assert.AreEqual(allocated, (await clock.ObserveAsync()).State.Allocated);
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Established_mapping_cannot_hide_native_recovery_required(DatabaseProfileId profile)
    {
        var authority = new Authority();
        using var store = new FileTimestampStore(Path.Combine(_root, "clock"), "mapping-domain");
        var clock = new DatabaseSharedTimestampService(store);
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "database"), profile,
            authority.Lease, Routing, authority, Options, timestamps: clock);
        var capture = MappingCapture(profile, node.Domain.Generation);
        await node.InitializeApplicationMappingAsync(capture);
        long allocated = (await clock.ObserveAsync()).State.Allocated;
        node.Kernel.RequireRecovery("mapping-readiness-test");
        await Assert.ThrowsAsync<EntityForgeDbRecoveryRequiredException>(async () => await node.InitializeApplicationMappingAsync(capture));
        Assert.AreEqual(allocated, (await clock.ObserveAsync()).State.Allocated);
    }
}
