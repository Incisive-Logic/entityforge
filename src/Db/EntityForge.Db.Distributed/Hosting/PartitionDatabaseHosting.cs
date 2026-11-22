using EntityForge.Db.Gql;
using dotnet_etcd;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>The shared exact-request and coordinated-decision authority for a partition-position database.</summary>
/// <remarks>The caller owns the etcd connection and its authentication/TLS configuration.</remarks>
public sealed class PartitionDecisionAuthority
{
    internal IPartitionTransactionAuthority Inner
    {
        get;
    }

    internal PartitionDecisionAuthority(IPartitionTransactionAuthority authority) => Inner = authority;

    /// <summary>Creates or opens the dedicated, generation-bound etcd namespace. Incompatible unreleased formats are rejected.</summary>
    public static async ValueTask<PartitionDecisionAuthority> OpenEtcdAsync(EtcdClient client, string prefix,
        Guid generation, DatabaseProfileId profile, DistributedRoutingBinding routing, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(routing);
        return new(await GrpcEtcdPartitionTransactionAuthority.OpenAsync(client, prefix, generation, profile, routing, token));
    }

    /// <summary>Uses native transaction homes over this control authority. The directory must preserve physical identities and be populated before recovery/admission.</summary>
    public PartitionDecisionAuthority WithNativeHomes(DistributedRoutingBinding routing,
        Func<LogicalShardId, PartitionRemoteOwner> directory, HttpClient participantHttp, string participantBearer,
        IShardPlacementAuthority placements)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(participantHttp);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentException.ThrowIfNullOrWhiteSpace(participantBearer);
        if (Inner is PartitionNativeHomeAuthority)
        {
            throw new InvalidOperationException("Native homes are already selected.");
        }

        PartitionNativeHomeAuthority? homes = null;
        homes = new(Inner, routing, shard => PartitionDatabaseConnections.Connect(directory(shard), routing,
            participantHttp, participantBearer, placements, homes!.Identity));
        return new(homes);
    }
}

/// <summary>Explicit node configuration. The clock and evaluator lifetimes remain owned by the caller.</summary>
public sealed record PartitionDatabaseNodeOptions
{
    /// <summary>Required timestamp domain and monitored UTC bounds. Missing or unhealthy evidence refuses startup.</summary>
    public IDatabaseBoundedClock? TimestampClock
    {
        get; init;
    }

    /// <summary>Gets the optional governance evaluators used to validate semantic writes on this node.</summary>
    public IReadOnlyList<ISemanticGovernanceEvaluator> GovernanceEvaluators { get; init; } = [];

    internal ISemanticGovernanceEvaluator[] Validate(DatabaseProfileId profile)
    {
        PartitionTimestampClock.Require(TimestampClock);
        ArgumentNullException.ThrowIfNull(GovernanceEvaluators);
        var evaluators = GovernanceEvaluators.ToArray();
        if (evaluators.Any(e => e is null) || evaluators.Length != 0 && profile != DatabaseProfileId.Semantic)
        {
            throw new ArgumentException("Governance evaluators require a Semantic node and cannot contain null entries.");
        }

        return evaluators;
    }
}

internal static class PartitionDatabaseConnections
{
    internal static HttpPartitionTransactionParticipant Connect(PartitionRemoteOwner owner, DistributedRoutingBinding routing,
        HttpClient http, string bearer, IShardPlacementAuthority placements, string authority)
    {
        if (owner?.Identity is not { } identity || owner.Endpoint is null)
        {
            throw new ArgumentException("Missing physical owner.", nameof(owner));
        }

        if (identity.Routing != routing.Fingerprint || !routing.Shards.Contains(new(identity.Partition)))
        {
            throw new InvalidDataException("The physical owner belongs to another routing domain.");
        }

        var domain = new PartitionExecutionDomain(identity.Generation, new(identity.Partition), identity.Routing, identity.Profile, identity.Incarnation);
        return new(http, owner.Endpoint, bearer, domain, authority, async cancellation =>
        {
            var current = await placements.ResolveAsync(domain.Partition, cancellation)
                ?? throw new InvalidOperationException("The partition currently has no authoritative owner.");
            if (current.Claim.Generation.Value != identity.Generation || current.Claim.MapVersion != routing.MapVersion || current.Claim.Shard != domain.Partition)
            {
                throw new InvalidDataException("Refreshed placement belongs to another routing domain.");
            }

            return current.ParticipantEndpoint;
        });
    }
}
