using EntityForge.Db.Mapping;
using EntityForge.Db.Durability;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal interface IPartitionCoordinatorHomeParticipant
{
    PartitionExecutionDomain Domain
    {
        get;
    }

    ValueTask<DurablePartitionTransaction?> ReadHomeAsync(Guid operation, CancellationToken token);

    ValueTask<DurablePartitionTransaction> SelectHomeAsync(PartitionApplicationBinding application, PartitionTransactionPlan proposal, CancellationToken token);

    ValueTask<DurablePartitionTransaction> DecideHomeAsync(Guid operation, string descriptor, bool commit, CancellationToken token);

    ValueTask<DurablePartitionTransaction> DecideTimestampHomeAsync(Guid operation, string descriptor, DurableTimestampState time, CancellationToken token) =>
        ValueTask.FromException<DurablePartitionTransaction>(new NotSupportedException("Timestamp home decisions are unavailable."));

    IAsyncEnumerable<PartitionDecisionNotification> ReadHomeNotificationsAsync(CancellationToken token);

    ValueTask<DatabaseDistributedCommitReceipt> ExecuteHomeAsync(PartitionApplicationBinding application, CancellationToken token) =>
        ValueTask.FromException<DatabaseDistributedCommitReceipt>(new NotSupportedException("Complete home execution is unavailable."));
}

// Explicit experimental authority domain. Placement/enrollment remains in the
// control authority; operation selection and outcome live only at the native home.
internal sealed class PartitionNativeHomeAuthority(IPartitionTransactionAuthority control,
    DistributedRoutingBinding routing, Func<LogicalShardId, IPartitionCoordinatorHomeParticipant> resolve, bool executeAtHome = true)
    : IPartitionTransactionAuthority, IPartitionCombinedSelectionAuthority, IPartitionDecisionNotifications
{
    public string Identity { get; } = PartitionCoordinatorHome.Policy + ":" + control.Identity;

    internal DistributedRoutingBinding Routing => routing;

    internal bool ExecuteAtHome => executeAtHome;

    internal IPartitionTransactionParticipant Participant(LogicalShardId shard)
    {
        var participant = resolve(shard) as IPartitionTransactionParticipant
            ?? throw new NotSupportedException("The home requires a complete participant transport.");
        if (participant.Domain.Partition != shard || participant.Domain.RoutingFingerprint != routing.Fingerprint ||
            participant.TransactionAuthorityIdentity != Identity)
        {
            throw new InvalidDataException("Home participant association differs.");
        }

        return participant;
    }

    internal ValueTask<DatabaseDistributedCommitReceipt> ExecuteAsync(PartitionApplicationBinding application, CancellationToken token) =>
        Home(application.Operation).ExecuteHomeAsync(application, token);

    public ValueTask RequireUnenrolledAsync(PartitionAuthorityEnrollment partition, CancellationToken token = default) => control.RequireUnenrolledAsync(partition, token);

    public ValueTask EnrollAsync(PartitionExecutionDomain domain, bool create, CancellationToken token = default) => control.EnrollAsync(domain, create, token);

    private IPartitionCoordinatorHomeParticipant Home(Guid operation) => resolve(PartitionCoordinatorHome.Owner(operation, routing));

    private (PartitionApplicationBinding Application, PartitionTransactionPlan Plan) Manifest(DurablePartitionTransaction entry) =>
        PartitionCoordinatorHome.Manifest.Read(entry, Identity, routing, Home(entry.Operation).Domain);

    private static PartitionTransactionDecision? Decision(DurablePartitionTransaction? entry) => entry is null ||
        entry.State == DurablePartitionTransactionState.Prepared ? null : new(entry.Operation, entry.Descriptor,
            entry.State == DurablePartitionTransactionState.Committed, Timestamp: entry.Timestamp);

    public async ValueTask<PartitionTransactionPlan?> SelectCombinedAsync(PartitionApplicationBinding application,
        PartitionTransactionPlan proposal, CancellationToken token = default)
    {
        var home = Home(proposal.Operation);
        if (!proposal.Members.Any(member => member.Domain == home.Domain))
        {
            throw new NotSupportedException("The immutable home must participate in this experimental transaction.");
        }

        var selected = await home.SelectHomeAsync(application, proposal, token);
        return Manifest(selected).Plan;
    }

    public async ValueTask BindApplicationAsync(PartitionApplicationBinding binding, CancellationToken token = default)
    {
        var existing = await ReadApplicationAsync(binding.Operation, token);
        if (existing is null)
        {
            throw new NotSupportedException("Native-home selection requires the complete bounded proposal; no central fallback is permitted.");
        }

        if (existing.Descriptor != binding.Descriptor)
        {
            throw new EntityForgeDbIdempotencyException(binding.Operation);
        }
    }

    public async ValueTask BindAsync(PartitionTransactionPlan plan, CancellationToken token = default)
    {
        var selected = await ReadPlanAsync(plan.Operation, token);
        if (selected?.Descriptor != plan.Descriptor)
        {
            throw new InvalidDataException("No matching native-home selection.");
        }
    }

    public async ValueTask<PartitionApplicationBinding?> ReadApplicationAsync(Guid operation, CancellationToken token = default)
    {
        var entry = await Home(operation).ReadHomeAsync(operation, token);
        return entry is null ? null : Manifest(entry).Application;
    }

    public async ValueTask<PartitionTransactionPlan?> ReadPlanAsync(Guid operation, CancellationToken token = default)
    {
        var entry = await Home(operation).ReadHomeAsync(operation, token);
        return entry is null ? null : Manifest(entry).Plan;
    }

    public async ValueTask<PartitionTransactionDecision?> ReadDecisionAsync(Guid operation, CancellationToken token = default) =>
        Decision(await Home(operation).ReadHomeAsync(operation, token));

    internal async ValueTask<DurableTimestampState?> ReadTimestampVoteAsync(Guid operation, CancellationToken token) =>
        (await Home(operation).ReadHomeAsync(operation, token))?.Timestamp;

    public ValueTask<PartitionTransactionDecision> DecideTimestampedAsync(Guid operation, string descriptor, DurableTimestampState time, CancellationToken token = default) =>
        DecideCoreAsync(operation, descriptor, true, time, token);

    public ValueTask<PartitionTransactionDecision> DecideAsync(Guid operation, string descriptor, bool commit, CancellationToken token = default) =>
        DecideCoreAsync(operation, descriptor, commit, null, token);

    private async ValueTask<PartitionTransactionDecision> DecideCoreAsync(Guid operation, string descriptor, bool commit, DurableTimestampState? time, CancellationToken token)
    {
        var home = Home(operation);
        try
        {
            return Decision(time is null ? await home.DecideHomeAsync(operation, descriptor, commit, token) : await home.DecideTimestampHomeAsync(operation, descriptor, time, token))
                ?? throw new InvalidDataException("Home did not establish a terminal outcome.");
        }
        catch (EntityOperationCommittedException<DatabaseCommitReceipt> failure)
        {
            if (failure.OperationId != operation || failure.DatabaseIncarnation != home.Domain.Incarnation ||
                failure.NativeReceipt.Profile != home.Domain.Profile)
            {
                throw new InvalidDataException("Home commitment association differs.", failure);
            }
            // The exact home native commit IS the distributed commit decision.
            // Failure of publication/placement confirmation must not erase it.
            throw new PartitionDecisionDeliveryException(new(operation, descriptor, true, Timestamp: time), failure);
        }
    }

    public async ValueTask<PartitionTransactionDecision> AbortApplicationAsync(Guid operation, string applicationDescriptor, CancellationToken token = default)
    {
        var entry = await Home(operation).ReadHomeAsync(operation, token) ?? throw new InvalidDataException("Missing native home selection.");
        if (entry.Coordinator!.ApplicationDescriptor != applicationDescriptor)
        {
            throw new EntityForgeDbIdempotencyException(operation);
        }

        return await DecideAsync(operation, entry.Descriptor, false, token);
    }

    public async ValueTask<IReadOnlyList<PartitionTerminalObservation>> ReadTerminalAsync(IReadOnlyList<Guid> operations,
        CancellationToken token = default, IReadOnlyDictionary<Guid, string>? selected = null)
    {
        PartitionTerminalObservation.RequireBatch(operations);
        var descriptors = PartitionTerminalObservation.CopySelections(operations, selected);
        return await Task.WhenAll(operations.Select(async operation =>
        {
            try
            {
                var entry = await Home(operation).ReadHomeAsync(operation, token);
                if (entry is null)
                {
                    return new PartitionTerminalObservation(operation, null, null, null);
                }

                if (descriptors.TryGetValue(operation, out var descriptor))
                {
                    if (entry.Descriptor != descriptor)
                    {
                        throw new InvalidDataException("Home decision differs from native selection.");
                    }

                    return new(operation, null, null, Decision(entry));
                }
                var manifest = Manifest(entry);
                return new(operation, manifest.Application, manifest.Plan, Decision(entry));
            }
            catch (Exception failure)
            {
                return new PartitionTerminalObservation(operation, null, null, null, failure);
            }
        }));
    }

    public ValueTask RetainAsync(long cutoffMilliseconds, CancellationToken token = default) => control.RetainAsync(cutoffMilliseconds, token);

    public async IAsyncEnumerable<PartitionDecisionNotification> ReadNotificationsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var incoming = System.Threading.Channels.Channel.CreateBounded<PartitionDecisionNotification>(PartitionHomeNotificationHub.MaximumBuffered);
        async Task Pump(IPartitionCoordinatorHomeParticipant home)
        {
            try
            {
                await foreach (var notification in home.ReadHomeNotificationsAsync(lifetime.Token))
                {
                    await incoming.Writer.WriteAsync(notification, lifetime.Token);
                }

                throw new IOException("Native home notification source ended.");
            }
            catch (Exception failure)
            {
                incoming.Writer.TryComplete(failure);
            }
        }
        var sources = new List<Task>();
        try
        {
            foreach (var shard in routing.Shards)
            {
                sources.Add(Pump(resolve(shard)));
            }

            await foreach (var notification in incoming.Reader.ReadAllAsync(token))
            {
                yield return notification;
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            await Task.WhenAll(sources);
        }
    }
}

internal sealed partial class PartitionOwnedNode : IPartitionCoordinatorHomeParticipant
{
    ValueTask<DatabaseDistributedCommitReceipt> IPartitionCoordinatorHomeParticipant.ExecuteHomeAsync(PartitionApplicationBinding application, CancellationToken token) =>
        ExecuteCoordinatorHomeAsync(TransactionAuthorityIdentity!, application, token);

    private readonly PartitionHomeNotificationHub _homeNotifications = new();

    public IAsyncEnumerable<PartitionDecisionNotification> ReadHomeNotificationsAsync(CancellationToken token) =>
        _homeNotifications.ReadNotificationsAsync(token);

    PartitionExecutionDomain IPartitionCoordinatorHomeParticipant.Domain => Domain;

    ValueTask<DurablePartitionTransaction?> IPartitionCoordinatorHomeParticipant.ReadHomeAsync(Guid operation, CancellationToken token) =>
        ReadCoordinatorHomeAsync(TransactionAuthorityIdentity!, operation, token);

    ValueTask<DurablePartitionTransaction> IPartitionCoordinatorHomeParticipant.SelectHomeAsync(PartitionApplicationBinding application,
        PartitionTransactionPlan plan, CancellationToken token) => SelectCoordinatorHomeAsync(TransactionAuthorityIdentity!, application, plan, token);

    ValueTask<DurablePartitionTransaction> IPartitionCoordinatorHomeParticipant.DecideHomeAsync(Guid operation, string descriptor, bool commit, CancellationToken token) =>
        DecideCoordinatorHomeAsync(TransactionAuthorityIdentity!, operation, descriptor, commit, token);

    ValueTask<DurablePartitionTransaction> IPartitionCoordinatorHomeParticipant.DecideTimestampHomeAsync(Guid operation, string descriptor, DurableTimestampState time, CancellationToken token) =>
        DecideCoordinatorHomeAsync(TransactionAuthorityIdentity!, operation, descriptor, true, token, time);
}
