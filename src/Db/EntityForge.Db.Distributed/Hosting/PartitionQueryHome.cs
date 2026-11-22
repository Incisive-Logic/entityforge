using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// Internal completion capability: configured from the same actual placement
// authority as receiving owners. No caller-supplied endpoint is authority proof.
internal sealed class PartitionQueryHomeAuthority : IAsyncDisposable
{
    internal PartitionQueryAuthorityBinding Binding
    {
        get;
    }

    private readonly PartitionQueryServingGrants _serving;
    private readonly Guid _clockDomain;

    internal long ServingAcquisitions => _serving.Acquisitions;

    private long _validatedCompletions;

    internal long ValidatedCompletions => Interlocked.Read(ref _validatedCompletions);

    private PartitionQueryHomeAuthority(GrpcEtcdShardPlacementAuthority authority, PartitionQueryAuthorityBinding binding,
        EntityForge.Db.Kernel.IDatabaseBoundedClock clock, PartitionQueryServingOptions options)
    {
        Binding = binding;
        _clockDomain = clock.Domain;
        _serving = new(authority, binding, clock, options);
    }

    internal static async ValueTask<PartitionQueryHomeAuthority> OpenAsync(GrpcEtcdShardPlacementAuthority authority,
        EntityForge.Db.Kernel.IDatabaseBoundedClock clock, PartitionQueryServingOptions? options = null, CancellationToken token = default)
    {
        PartitionTimestampClock.Require(clock);
        return new(authority, await authority.QueryBindingAsync(token), clock, options ?? PartitionQueryServingOptions.Default);
    }

    private void RequirePending(PartitionQueryPendingCompletion p)
    {
        if (p.Authority != Binding || p.Job == Guid.Empty || p.Cut.Id == Guid.Empty || p.Sequence <= 0 ||
            string.IsNullOrWhiteSpace(p.Selection) || p.Cut.Expires <= DateTimeOffset.UtcNow ||
            p.ReadTime is not { Mvcc: true } time || time.Domain != _clockDomain || p.Cut.ClockDomain != _clockDomain ||
            p.Claim.Shard.Value != p.Cut.Position.Identity.Partition ||
            p.Claim.Generation.Value != p.Cut.Position.Identity.Generation ||
            p.Claim.MapVersion.Value <= 0 || p.Claim.FencingToken.Value <= 0)
        {
            throw new InvalidDataException("Pending completion authority, timestamp, association or lifetime differs.");
        }
    }

    internal async ValueTask ValidateAsync(IEnumerable<PartitionQueryPendingCompletion> completed, CancellationToken token)
    {
        using var trace = DistributedReadTelemetry.Start("partition.query.home.authority");
        var pending = completed.OrderBy(p => p.Claim.Shard.Value, StringComparer.Ordinal).ToArray();
        foreach (var item in pending)
        {
            RequirePending(item);
        }

        var claims = pending.Select(p => p.Claim).ToArray();
        trace?.SetTag("entityforge.claims", claims.Length);
        if (claims.Length is < 1 or > GrpcEtcdShardPlacementAuthority.MaximumQueryClaims ||
            claims.Select(c => c.Shard).Distinct().Count() != claims.Length)
        {
            throw new InvalidDataException("Invalid completed authority set.");
        }

        await _serving.ValidateAsync(claims, token);
        // Acquiring/refreshing authority can outlive a completed owner's cut.
        // It cannot extend that query's original protected lifetime.
        foreach (var item in pending)
        {
            RequirePending(item);
        }

        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _validatedCompletions);
    }

    public ValueTask DisposeAsync() => _serving.DisposeAsync();
}

internal sealed record PartitionQueryPendingCompletion(Guid Job, PartitionReadCut Cut, string Selection,
    int Sequence, ShardPlacementClaim Claim, PartitionQueryAuthorityBinding Authority,
    EntityForge.Db.Durability.DurableTimestampState? ReadTime = null)
{
    internal static string Fingerprint(PartitionQueryExchangeRequest selection) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(selection with
        {
            Offset = 0,
            GraphFrontier = null
        }, PartitionQueryJson.Options))));
}
