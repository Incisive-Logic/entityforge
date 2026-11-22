using EntityForge.Db.Mapping;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// Direct mapped execution revalidates source and policy closure under native admission.
internal sealed class PartitionOwnedSemanticExecution : IAsyncDisposable
{
    private readonly ISemanticEntityClient _client;
    private readonly PartitionOwnedExecution _execution;

    internal PartitionExecutionDomain Domain => _execution.Domain;

    internal PartitionOwnedSemanticExecution(PartitionExecutionDomain domain, DistributedRoutingBinding routing,
        ISemanticEntityClient client, PartitionExecutionAuthority authority, EntityForge.Db.Kernel.DatabaseKernel kernel, ISemanticDatabase? database = null)
    {
        if (domain.Profile != DatabaseProfileId.Semantic || !routing.Shards.Contains(domain.Partition) ||
            routing.Shards.Count != 1 && database is null ||
            routing.Fingerprint != domain.RoutingFingerprint || routing.StorageLayout != DatabaseStorageLayout.Current)
        {
            throw new NotSupportedException("Semantic local execution requires enrolled native ownership and commit-time closure validation.");
        }

        _client = client;
        _execution = new(domain, new Adapter(client, database, routing, domain.Partition), authority: authority,
            admitRead: () => kernel.PartitionTransactions.AdmitReadAsync());
    }

    internal SemanticEntityRequest CaptureSave<T>(T value, SemanticEntityWriteOperation operation) where T : class =>
        _client.EntityStore<T>().CaptureSave(value, operation);

    internal ValueTask<PartitionCommitResult> ExecuteAsync(PartitionExecutionDomain domain, Guid operation,
        ReadOnlyMemory<byte> captured, CancellationToken token = default) => _execution.ExecuteAsync(domain, operation, captured, token);

    internal ValueTask<SemanticEntityReadSnapshot<T>?> ReadAsync<T>(PartitionExecutionDomain domain, object identity,
        PartitionReadToken? minimum = null, CancellationToken token = default) where T : class => _execution.ReadAsync(domain, minimum, async () =>
        {
            var result = await _client.EntityStore<T>().ReadOnlyAsync(identity, CancellationToken.None).ConfigureAwait(false);
            if (result is null)
            {
                throw new InvalidOperationException("The bounded Semantic read requires an existing entity.");
            }

            return ((SemanticEntityReadSnapshot<T>?)result, result.SnapshotVersion);
        }, token);

    internal sealed class Adapter(ISemanticEntityClient client, ISemanticDatabase? database, DistributedRoutingBinding routing, LogicalShardId owner) : IPartitionOwnedExecutionAdapter
    {
        public DatabaseProfileId Profile => DatabaseProfileId.Semantic;

        public Guid Incarnation => client.DatabaseIncarnation;

        public async ValueTask<DatabaseCommitReceipt> ExecuteAsync(Guid operation, ReadOnlyMemory<byte> ownedRequest)
        {
            var request = SemanticEntityRequest.Restore(ownedRequest.Span);
            if (request.OperationId != operation || request._metadata.DatabaseIncarnation != Incarnation)
            {
                throw new InvalidDataException("Captured Semantic identity differs.");
            }

            try
            {
                if (routing.Shards.Count == 1)
                {
                    return (await client.DeliverAsync(request, CancellationToken.None)).Receipt;
                }

                if (database is not SemanticDatabaseEngine engine || PartitionSemanticOwnerPlan.Owner(request, routing) != owner)
                {
                    throw new InvalidDataException("Wrong native Semantic owner.");
                }

                return (await request.DeliverCoreAsync(database, CancellationToken.None,
                    (session, validate, token) => engine.ApplyOwnerMappedSessionAsync(session, validate,
                        (state, changes) => PartitionSemanticOwnerPlan.Validate(state, session, changes, routing, owner), token),
                    new EntityForge.Db.Semantic.Durability.SemanticOwnerCoverage(routing.Fingerprint, [owner.Value]))).Receipt;
            }
            catch (EntityOperationCommittedException<SessionCommitResult> known)
            {
                var receipt = known.NativeReceipt;
                if (receipt.TransactionId != operation || receipt.DatabaseIncarnation != Incarnation || receipt.Profile != Profile)
                {
                    throw new InvalidDataException("Semantic commitment association differs.", known);
                }

                throw new EntityOperationCommittedException<DatabaseCommitReceipt>(receipt, receipt, known);
            }
        }
    }

    public ValueTask DisposeAsync() => _execution.DisposeAsync();
}
