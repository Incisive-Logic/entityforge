using System.Collections.Immutable;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Kernel;

namespace EntityForge.Db.Distributed.Hosting;

// A cache of authoritatively acquired control capabilities, never data or a
// positive observation TTL. Replacement is prevented by etcd for each capability.
internal sealed class PartitionQueryServingGrants : IAsyncDisposable
{
    private readonly GrpcEtcdShardPlacementAuthority _authority;
    private readonly PartitionQueryAuthorityBinding _binding;
    private readonly IDatabaseBoundedClock _clock;
    private readonly Guid _clockDomain;
    private readonly PartitionQueryServingOptions _options;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private ImmutableDictionary<LogicalShardId, PartitionServingGrant> _grants = ImmutableDictionary<LogicalShardId, PartitionServingGrant>.Empty;
    private int _disposed;
    private long _acquisitions;

    internal long Acquisitions => Interlocked.Read(ref _acquisitions);

    internal PartitionQueryServingGrants(GrpcEtcdShardPlacementAuthority authority, PartitionQueryAuthorityBinding binding,
        IDatabaseBoundedClock clock, PartitionQueryServingOptions options)
    {
        options.Validate();
        _authority = authority;
        _binding = binding;
        _clock = clock;
        _clockDomain = clock.Domain;
        _options = options;
        if (_clockDomain == Guid.Empty)
        {
            throw new ArgumentException("A serving clock domain is required.");
        }
    }

    private bool Covers(IReadOnlyList<ShardPlacementClaim> claims)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_clock.Domain != _clockDomain)
        {
            throw new InvalidOperationException("Serving clock domain changed.");
        }

        var now = GrpcEtcdShardPlacementAuthority.ReadServingClock(_clock, _clockDomain);
        var grants = Volatile.Read(ref _grants);
        foreach (var claim in claims)
        {
            if (!grants.TryGetValue(claim.Shard, out var grant) || grant.Claim != claim || grant.ClockDomain != _clockDomain ||
                now.LatestUtcTicks >= grant.UntilUtcTicks - _options.RenewalMargin.Ticks)
            {
                return false;
            }
        }

        return true;
    }

    internal async ValueTask ValidateAsync(IReadOnlyList<ShardPlacementClaim> claims, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Covers(claims))
        {
            return;
        }

        await _refresh.WaitAsync(token);
        try
        {
            if (Covers(claims))
            {
                return;
            }

            var grants = await _authority.AcquireQueryServingGrantsAsync(_binding, claims, _clock,
                _options.Lifetime, _options.RenewalMargin, token);
            var next = _grants;
            if (next.Count + grants.Count(g => !next.ContainsKey(g.Claim.Shard)) > GrpcEtcdShardPlacementAuthority.MaximumQueryClaims)
            {
                next = ImmutableDictionary<LogicalShardId, PartitionServingGrant>.Empty;
            }

            foreach (var grant in grants)
            {
                next = next.SetItem(grant.Claim.Shard, grant);
            }

            Volatile.Write(ref _grants, next);
            Interlocked.Increment(ref _acquisitions);
            // Freshly acquired and cached certificates pass the SAME clock check.
            if (!Covers(claims))
            {
                throw new InvalidOperationException("Serving authority expired during acquisition.");
            }

            token.ThrowIfCancellationRequested();
        }
        finally
        {
            _refresh.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        await _refresh.WaitAsync();
        try
        {
            Volatile.Write(ref _grants, ImmutableDictionary<LogicalShardId, PartitionServingGrant>.Empty);
        }
        finally
        {
            _refresh.Release();
        }
        // Do not revoke shared grants. Do not dispose the semaphore while a
        // concurrent waiter can still wake and discharge its failed admission.
    }
}
