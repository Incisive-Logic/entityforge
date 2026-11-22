namespace EntityForge.Db.Distributed.Hosting;

// The coordinator uses the same participant contract in-process and over HTTP.
// Neither transport is allowed to allocate a global native version.
internal interface IPartitionTransactionParticipant
{
    // Every implementation must preserve this evidence, including forwarding
    // adapters. Defaulting to null could hide a timestamp-enabled native owner.
    ValueTask<EntityForge.Db.Durability.DurableTimestampState?> PrepareWithTimestampAsync(PartitionTransactionPlan plan, CancellationToken token);

    ValueTask<EntityForge.Db.Durability.DurableTimestampState> PrepareTimestampNativeAsync(PartitionTransactionPlan plan, CancellationToken token) =>
        ValueTask.FromException<EntityForge.Db.Durability.DurableTimestampState>(new NotSupportedException("Timestamp preparation is unavailable."));

    PartitionExecutionDomain Domain
    {
        get;
    }

    string? TransactionAuthorityIdentity
    {
        get;
    }

    ValueTask<bool> CanExecuteOwnerSemanticAsync(ReadOnlyMemory<byte> captured, CancellationToken token = default) => ValueTask.FromResult(false);

    ValueTask<bool> CanExecuteOwnerGraphAsync(ReadOnlyMemory<byte> captured, CancellationToken token = default) => ValueTask.FromResult(false);

    ValueTask<PartitionCommitResult> ExecuteMappedAsync(Guid operation, ReadOnlyMemory<byte> captured, CancellationToken token = default);

    ValueTask<PartitionCommitResult> ExecuteApplicationAsync(Guid operation, CancellationToken token = default);

    ValueTask<PartitionTransactionMember> PlanApplicationAsync(Guid operation, CancellationToken token = default);

    bool SupportsApplicationProposal => false;

    bool SupportsSemanticProposal => false;

    ValueTask<PartitionSemanticProposal> ProposeSemanticAsync(byte[] captured, TimeSpan duration, CancellationToken token = default) =>
        ValueTask.FromException<PartitionSemanticProposal>(new NotSupportedException("Owner Semantic proposals are unavailable."));

    ValueTask<PartitionTransactionMember> ProposeApplicationAsync(PartitionApplicationBinding proposal, CancellationToken token = default) =>
        throw new NotSupportedException("This participant requires a previously selected application.");

    ValueTask PrepareLocalApplicationAsync(Guid operation, CancellationToken token = default);

    ValueTask InitializeApplicationMappingAsync(ReadOnlyMemory<byte> captured, CancellationToken token = default);

    ValueTask<PartitionMemberResolution> ResolveApplicationAsync(Guid operation, bool abort, CancellationToken token = default);

    ValueTask PrepareNativeAsync(PartitionTransactionPlan plan, CancellationToken token = default);

    ValueTask<DatabaseCommitReceipt?> ApplyPartitionDecisionAsync(Guid operation, CancellationToken token = default);
}
