using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed class PartitionOwnedRdfExecution : IAsyncDisposable
{
    private readonly IRdfEntityClient _client;
    private readonly DistributedRoutingBinding _routing;
    private readonly PartitionOwnedExecution _execution;

    internal PartitionExecutionDomain Domain => _execution.Domain;

    internal PartitionOwnedRdfExecution(Guid generation, LogicalShardId owner, DistributedRoutingBinding routing,
        IRdfDatabase database, PartitionExecutionAuthority authority)
    {
        if (database is not RdfDatabaseEngine native || native.PartitionKernel.VersioningMode != DatabaseVersioningMode.PartitionLocal ||
            routing.StorageLayout != DatabaseStorageLayout.Current || !routing.Shards.Contains(owner))
        {
            throw new NotSupportedException("The RDF adapter requires a profile-owned partition-local kernel.");
        }

        _client = database.Operations();
        _routing = routing;
        _execution = new(new(generation, owner, routing.Fingerprint, DatabaseProfileId.Rdf, _client.DatabaseIncarnation),
            new Adapter(this), authority: authority, admitRead: () => native.PartitionKernel.PartitionTransactions.AdmitReadAsync());
    }

    internal RdfEntityRequest CaptureSave<T>(T value, Guid operation) => _client.EntityStore<T>().CaptureSave(value, new(operation));

    internal ValueTask<PartitionCommitResult> ExecuteAsync(PartitionExecutionDomain domain, Guid operation,
        ReadOnlyMemory<byte> captured, CancellationToken token = default) => _execution.ExecuteAsync(domain, operation, captured, token);

    internal ValueTask<EntityReadResult<T>?> ReadAsync<T>(PartitionExecutionDomain domain, object identity,
        PartitionReadToken? minimum = null, CancellationToken token = default) => _execution.ReadAsync(domain, minimum, async () =>
        {
            var store = _client.EntityStore<T>();
            RequireScalar(store.Mapping.Manifest);
            RequireOwner(store.CaptureDelete(identity, new(Guid.NewGuid())));
            var result = await store.ReadAsync(identity, CancellationToken.None).ConfigureAwait(false);
            if (result is null)
            {
                throw new InvalidOperationException("The bounded RDF read requires an existing subject.");
            }

            return ((EntityReadResult<T>?)result, result.SnapshotVersion);
        }, token);

    private static void RequireScalar(EntityMappingManifest manifest)
    {
        if (manifest.Profile != DatabaseProfileId.Rdf || manifest.Members.Any(member => member.Kind == EntityMemberKind.Reference))
        {
            throw new NotSupportedException("Partition-local RDF mapped execution currently admits scalar default-subject mappings.");
        }
    }

    private void RequireOwner(RdfEntityRequest request)
    {
        if (CapturedOwner(request) != Domain.Partition)
        {
            throw new InvalidDataException("The complete mapped subject belongs to another partition.");
        }
    }

    internal LogicalShardId CapturedOwner(RdfEntityRequest request)
    {
        var subject = RdfEntityInputCompiler.FromManifest(request.Captured.Description.Manifest).CapturedRoot(request.Captured);
        return _routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(subject));
    }

    private sealed class Adapter(PartitionOwnedRdfExecution owner) : IPartitionOwnedExecutionAdapter
    {
        public DatabaseProfileId Profile => DatabaseProfileId.Rdf;

        public Guid Incarnation => owner._client.DatabaseIncarnation;

        public ValueTask<DatabaseCommitReceipt> ExecuteAsync(Guid operation, ReadOnlyMemory<byte> ownedRequest)
        {
            if (PartitionOwnerOperation.IsOwnerLocal(operation) &&
                PartitionOwnerOperation.Owner(operation, owner._routing) != owner.Domain.Partition)
            {
                throw new InvalidDataException("The operation identity belongs to another partition.");
            }

            var request = RdfEntityRequest.Restore(ownedRequest.ToArray());
            if (request.OperationId != operation || request.Captured.Description.DatabaseIncarnation != Incarnation)
            {
                throw new InvalidDataException("Captured RDF identity differs.");
            }

            if (request.Captured.Description.Manifest.Profile != DatabaseProfileId.Rdf)
            {
                throw new InvalidDataException("The mapping is not RDF.");
            }

            owner.RequireOwner(request);
            // The native compiler reads only this default-graph subject and emits
            // assertions/retractions for it. Resource-valued predicates also belong
            // to this subject; they do not require object-owner reads or mutations.
            // This does not admit recursive reference reads or broad patterns.
            return owner._client.DeliverAsync(request, CancellationToken.None);
        }
    }

    public ValueTask DisposeAsync() => _execution.DisposeAsync();
}
