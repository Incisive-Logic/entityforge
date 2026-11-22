using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// Experimental fixed-owner adapter. The composition owns an isolated native
// database; it must never enroll this database in the global-version protocol.
internal sealed class PartitionOwnedGraphExecution : IAsyncDisposable
{
    private readonly IGraphEntityClient _client;
    private readonly IGraphDatabase _database;
    private readonly DistributedRoutingBinding _routing;
    private readonly PartitionOwnedExecution _execution;

    internal PartitionExecutionDomain Domain => _execution.Domain;

    internal GraphEntityRequest CaptureSave<T>(T value, Guid operation) =>
        _client.EntityStore<T>().CaptureSave(value, new GraphEntityWriteOptions(operation));

    internal PartitionOwnedGraphExecution(Guid generation, LogicalShardId owner,
        DistributedRoutingBinding routing, IGraphDatabase database, PartitionExecutionAuthority? authority = null)
    {
        if (database is not GraphDatabaseEngine { UsesPartitionLocalVersioning: true } native)
        {
            throw new NotSupportedException("The Graph adapter requires an explicitly partition-local kernel composition.");
        }

        if (!routing.Shards.Contains(owner) || routing.StorageLayout != DatabaseStorageLayout.Current)
        {
            throw new NotSupportedException("Only a fixed profile-owned Graph partition is supported.");
        }

        _client = database.Operations();
        _database = database;
        _routing = routing;
        var domain = new PartitionExecutionDomain(generation, owner, routing.Fingerprint,
            DatabaseProfileId.PropertyGraph, _client.DatabaseIncarnation);
        _execution = new(domain, new Adapter(this), authority: authority,
            admitRead: () => native.PartitionKernel.PartitionTransactions.AdmitReadAsync());
    }

    internal ValueTask<PartitionCommitResult> ExecuteAsync(PartitionExecutionDomain domain, Guid operation,
        ReadOnlyMemory<byte> captured, CancellationToken token = default) => _execution.ExecuteAsync(domain, operation, captured, token);

    internal ValueTask<EntityReadResult<T>?> ReadAsync<T>(PartitionExecutionDomain domain, object identity,
        PartitionReadToken? minimum = null, CancellationToken token = default) => _execution.ReadAsync(domain, minimum, async () =>
        {
            var store = _client.EntityStore<T>();
            RequireLocalMapping(store.Mapping.Manifest);
            var captured = store.CaptureDelete(identity, new(Guid.NewGuid()));
            RequireOwner(captured);
            // ReadAsync validates and discharges its retained native scope before
            // the common core can return the value or its version assertion.
            var value = await store.ReadAsync(identity, CancellationToken.None).ConfigureAwait(false);
            if (value is null)
            {
                throw new InvalidOperationException("The bounded scalar read requires an existing owned vertex.");
            }

            return ((EntityReadResult<T>?)value, value.SnapshotVersion);
        }, token);

    private void RequireLocalMapping(EntityMappingManifest manifest)
    {
        if (manifest.Profile != DatabaseProfileId.PropertyGraph ||
            _routing.Shards.Count != 1 && manifest.Members.Any(m => m.Kind == EntityMemberKind.Reference))
        {
            throw new NotSupportedException("The complete Graph reference closure is not confined to this routing domain.");
        }
    }

    private void RequireOwner(GraphEntityRequest request)
    {
        if (CapturedOwner(request) != Domain.Partition)
        {
            throw new InvalidDataException("The captured root belongs to another partition.");
        }
    }

    internal LogicalShardId CapturedOwner(GraphEntityRequest request)
    {
        var root = GraphEntityInputCompiler.FromManifest(request.Captured.Description.Manifest).CapturedRoot(request.Captured);
        return _routing.RouteGraphVertex(root.Value);
    }

    private sealed class Adapter(PartitionOwnedGraphExecution owner) : IPartitionOwnedExecutionAdapter
    {
        public DatabaseProfileId Profile => DatabaseProfileId.PropertyGraph;

        public Guid Incarnation => owner._client.DatabaseIncarnation;

        public ValueTask<DatabaseCommitReceipt> ExecuteAsync(Guid operation, ReadOnlyMemory<byte> ownedRequest)
        {
            if (PartitionOwnerOperation.IsOwnerLocal(operation) &&
                PartitionOwnerOperation.Owner(operation, owner._routing) != owner.Domain.Partition)
            {
                throw new InvalidDataException("The operation identity belongs to another partition.");
            }

            var request = GraphEntityRequest.Restore(ownedRequest.ToArray());
            if (request.OperationId != operation || request.Captured.Description.DatabaseIncarnation != Incarnation)
            {
                throw new InvalidDataException("Captured request identity differs.");
            }

            if (owner._routing.Shards.Count != 1 && request.Captured.Description.Manifest.Members.Any(m => m.Kind == EntityMemberKind.Reference))
            {
                owner.RequireOwner(request);
                var root = GraphEntityInputCompiler.FromManifest(request.Captured.Description.Manifest).CapturedRoot(request.Captured);
                return request.DeliverCoreAsync(owner._database, CancellationToken.None,
                    token => PartitionGraphOwnerPlan.PrepareAsync(owner._database, request, token),
                    write => PartitionGraphOwnerPlan.Validate(write, root));
            }
            owner.RequireLocalMapping(request.Captured.Description.Manifest);
            if (request.Captured.Description.Delete && owner._routing.Shards.Count != 1)
            {
                throw new NotSupportedException("Graph deletion requires its complete adjacency closure.");
            }

            owner.RequireOwner(request);
            // Scalar writes are vertex-local. With exactly one enrolled shard,
            // native mapping also owns every endpoint and adjacency dependency.
            return owner._client.DeliverAsync(request, CancellationToken.None);
        }
    }

    public ValueTask DisposeAsync() => _execution.DisposeAsync();
}
