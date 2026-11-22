using System.Text.Json;
using EntityForge.Db.Durability;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal interface IPartitionMaintenanceParticipant : IPartitionReadParticipant
{
    ValueTask PrepareMaintenanceAsync(CancellationToken token = default) => ValueTask.CompletedTask;

    string? TransactionAuthorityIdentity
    {
        get;
    }

    ValueTask<PartitionReadCut> AcquireMaintenanceAsync(Guid id, DateTimeOffset expires, CancellationToken token = default);

    ValueTask<byte[]> MaintainAsync(PartitionReadCut lease, PartitionMaintenanceCommand command, CancellationToken token = default);
}

internal sealed partial class PartitionOwnedNode : IPartitionMaintenanceParticipant
{
    string? IPartitionMaintenanceParticipant.TransactionAuthorityIdentity => TransactionAuthorityIdentity;

    public ValueTask PrepareMaintenanceAsync(CancellationToken token = default) => Kernel.PartitionTransactions.PrepareCheckpointChangesAsync(token);

    public ValueTask<PartitionReadCut> AcquireMaintenanceAsync(Guid id, DateTimeOffset expires, CancellationToken token = default) =>
        _readCuts.AcquireAsync(id, expires, token, maintenance: true);

    public ValueTask<byte[]> MaintainAsync(PartitionReadCut lease, PartitionMaintenanceCommand command, CancellationToken token = default) =>
        _readCuts.MaintainAsync(lease, command, token);

    internal async ValueTask<byte[]> MaintainFrozenAsync(Maintenance handle, PartitionMaintenanceCommand command, CancellationToken token, CancellationToken lifetime)
    {
        if (handle.RetentionStarted && command.Action is not ("finish-retain" or "verify"))
        {
            throw new InvalidOperationException("The captured maintenance cut no longer admits frozen commands.");
        }

        if (command.Action == "pending" && command.Operation == Guid.Empty && command.Floor == 0)
        {
            return JsonSerializer.SerializeToUtf8Bytes((await handle.ReadPreparationsAsync(token))
                .Where(entry => entry.State == DurablePartitionTransactionState.Prepared).Select(entry => entry.Operation).ToArray());
        }

        if (command.Action == "home-abort" && command.Operation != Guid.Empty && command.Floor == 0)
        {
            await handle.AbortHomeAsync(command.Operation, token);
        }
        else if (command.Action == "apply" && command.Operation != Guid.Empty && command.Floor == 0)
        {
            var authority = _transactions ?? throw new InvalidOperationException("Missing transaction authority.");
            var plan = await authority.ReadPlanAsync(command.Operation, token) ?? throw new InvalidDataException("Missing selected plan.");
            (await authority.ReadApplicationAsync(command.Operation, token) ?? throw new InvalidDataException("Missing application selection.")).RequireCoordinatedPlan(plan);
            var decision = await authority.ReadDecisionAsync(command.Operation, token) ?? throw new InvalidOperationException("Undecided maintenance application.");
            await handle.ApplyAsync(plan, decision, token);
        }
        else if (command.Action == "retain" && command.Operation == Guid.Empty && command.Floor > 0)
        {
            await handle.RetainAsync(DateTimeOffset.FromUnixTimeMilliseconds(command.Floor), token);
        }
        else if (command.Action == "capture-retain" && command.Operation == Guid.Empty && command.Floor > 0)
        {
            await handle.BeginRetentionAsync(DateTimeOffset.FromUnixTimeMilliseconds(command.Floor), lifetime);
        }
        else if (command.Action == "finish-retain" && command.Operation == Guid.Empty && command.Floor == 0)
        {
            await handle.FinishRetentionAsync();
        }
        else if (command.Action == "verify" && command.Operation == Guid.Empty && command.Floor == 0)
        {
            await handle.VerifyAsync();
        }
        else
        {
            throw new InvalidDataException("Invalid partition maintenance command.");
        }

        return "null"u8.ToArray();
    }
}

internal static class PartitionRemoteRetention
{
    private static readonly System.Diagnostics.Metrics.Meter Meter = new("EntityForge.Db.Distributed.Retention");
    private static readonly System.Diagnostics.Metrics.Histogram<double> PhaseDuration =
        Meter.CreateHistogram<double>("entityforge.db.distributed.retention.phase.duration", "ms");

    private const int MaximumConcurrentRetentionOwners = 8;

    internal static void OwnerPhase(string phase, long started)
    {
        if (PhaseDuration.Enabled)
        {
            PhaseDuration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("phase", phase));
        }
    }

    internal static async ValueTask RetainAsync(IPartitionTransactionAuthority authority,
        IReadOnlyList<IPartitionMaintenanceParticipant> owners, DistributedRoutingBinding routing,
        DateTimeOffset retainedSince, CancellationToken token = default, Func<ValueTask>? captured = null)
    {
        long floor = retainedSince.ToUnixTimeMilliseconds();
        if (floor <= 0 || retainedSince > DateTimeOffset.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedSince));
        }

        if (owners.Count != routing.Shards.Count || owners.Count is < 1 or > DatabasePositionVector.MaximumPartitions ||
            owners.Any(owner => owner is null || owner.TransactionAuthorityIdentity != authority.Identity) ||
            !owners.Select(owner => owner.Domain.Partition).ToHashSet().SetEquals(routing.Shards))
        {
            throw new ArgumentException("The complete enrolled maintenance set is required.");
        }

        _ = new DatabasePositionVector(owners.Select(owner => new DatabasePartitionPosition(owner.Domain.Identity, new(0))).ToArray());
        if (owners.Any(owner => owner.Domain.RoutingFingerprint != routing.Fingerprint))
        {
            throw new InvalidDataException("Maintenance routing differs.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(PartitionReadCuts.MaximumDuration);
        var expires = DateTimeOffset.UtcNow + PartitionReadCuts.MaximumDuration;
        var attempted = new List<(IPartitionMaintenanceParticipant Owner, Guid Id)>();
        var handles = new Dictionary<IPartitionMaintenanceParticipant, PartitionReadCut>();
        var phaseWatch = PhaseDuration.Enabled ? System.Diagnostics.Stopwatch.StartNew() : null;
        void Phase(string name)
        {
            if (phaseWatch is null)
            {
                return;
            }

            PhaseDuration.Record(phaseWatch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("phase", name));
            phaseWatch.Restart();
        }
        try
        {
            // Prepare every owner before taking the first cut. Preparation owns no
            // admission fence and does not authorize retention or WAL reclamation.
            foreach (var owner in owners)
            {
                await owner.PrepareMaintenanceAsync(deadline.Token).ConfigureAwait(false);
            }

            Phase("prepare-owner-checkpoints");
            foreach (var owner in owners.OrderBy(owner => owner.Domain.Partition.Value, StringComparer.Ordinal))
            {
                var id = Guid.NewGuid();
                attempted.Add((owner, id));
                handles.Add(owner, await owner.AcquireMaintenanceAsync(id, expires, deadline.Token));
            }
            Phase("acquire-owner-cuts");
            var pending = new HashSet<Guid>();
            foreach (var pair in handles)
            {
                byte[] payload = await pair.Key.MaintainAsync(pair.Value, new("pending"), deadline.Token);
                var operations = JsonSerializer.Deserialize<Guid[]>(payload) ?? throw new InvalidDataException("Missing preparations.");
                if (operations.Length > DurablePartitionTransaction.MaximumTransactions || operations.Any(id => id == Guid.Empty))
                {
                    throw new InvalidDataException("Invalid pending preparation list.");
                }

                pending.UnionWith(operations);
            }
            Phase("read-pending");
            foreach (Guid operation in pending)
            {
                var plan = await authority.ReadPlanAsync(operation, deadline.Token) ?? throw new InvalidDataException("Prepared plan is missing.");
                if (authority is PartitionNativeHomeAuthority)
                {
                    var home = handles.Single(pair => pair.Key.Domain.Partition == PartitionCoordinatorHome.Owner(operation, routing));
                    await RequireEmptyAsync(home.Key, home.Value, new("home-abort", operation), deadline.Token);
                }
                else
                {
                    await authority.DecideAsync(operation, plan.Descriptor, false, deadline.Token);
                }

                foreach (var member in plan.Members)
                {
                    var pair = handles.Single(pair => pair.Key.Domain == member.Domain);
                    await RequireEmptyAsync(pair.Key, pair.Value, new("apply", operation), deadline.Token);
                }
            }
            Phase("resolve-pending");
            // All owners are frozen and pending applications are resolved. Each
            // native checkpoint is independent; shared authority expiry still
            // waits for every checkpoint. WhenAll drains started work before
            // the catch path can release any maintenance cut.
            foreach (var batch in handles.Chunk(MaximumConcurrentRetentionOwners))
            {
                await Task.WhenAll(batch.Select(pair => RequireEmptyAsync(pair.Key, pair.Value,
                    new(captured is null ? "retain" : "capture-retain", Floor: floor), deadline.Token).AsTask())).ConfigureAwait(false);
            }

            if (captured is not null)
            {
                Phase("capture-retention");
                await captured().ConfigureAwait(false);
                Phase("resume-admission");
                foreach (var batch in handles.Chunk(MaximumConcurrentRetentionOwners))
                {
                    await Task.WhenAll(batch.Select(pair => RequireEmptyAsync(pair.Key, pair.Value,
                        new("finish-retain"), deadline.Token).AsTask())).ConfigureAwait(false);
                }
            }
            Phase("finish-retention");
            foreach (var pair in handles)
            {
                await RequireEmptyAsync(pair.Key, pair.Value, new("verify"), deadline.Token);
            }

            Phase("verify-owners");
            await authority.RetainAsync(floor, deadline.Token);
            Phase("retain-authority");
            foreach (var clock in owners.OfType<IPartitionReadParticipant>().Select(o => o.SharedTimestamps)
                .OfType<EntityForge.Db.Kernel.DatabaseSharedTimestampService>().DistinctBy(c => c.Identity))
            {
                await clock.RetireAsync(retainedSince, deadline.Token);
            }

            Phase("retire-clocks");
            foreach (var pair in handles)
            {
                await pair.Key.CompleteCutAsync(pair.Value, deadline.Token);
            }

            Phase("complete-cuts");
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            foreach (var attempt in attempted)
            {
                try
                {
                    await attempt.Owner.AbandonCutAsync(attempt.Id, CancellationToken.None);
                }
                catch (Exception cleanup)
                {
                    failures.Add(cleanup);
                }
            }

            throw new AggregateException("Remote partition retention did not complete.", failures);
        }
    }

    private static async ValueTask RequireEmptyAsync(IPartitionMaintenanceParticipant owner, PartitionReadCut lease,
        PartitionMaintenanceCommand command, CancellationToken token)
    {
        var bytes = await owner.MaintainAsync(lease, command, token);
        if (!bytes.AsSpan().SequenceEqual("null"u8))
        {
            throw new InvalidDataException("Unexpected maintenance result.");
        }
    }
}
