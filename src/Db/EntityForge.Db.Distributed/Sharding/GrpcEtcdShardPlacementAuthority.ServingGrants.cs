using System.Text.Json;
using EntityForge.Db.Kernel;
using Etcdserverpb;
using Google.Protobuf;

namespace EntityForge.Db.Distributed.Sharding;

/// <summary>Indicates that an active serving grant prevents a placement-changing operation.</summary>
public sealed class ShardServingGrantActiveException(LogicalShardId shard)
    : Exception($"Shard '{shard.Value}' cannot be reassigned while a read-serving grant is live.");

internal sealed record PartitionServingGrant(ShardPlacementClaim Claim, Guid ClockDomain, long UntilUtcTicks);

public sealed partial class GrpcEtcdShardPlacementAuthority
{
    // This key has a separate, never-renewed lease. Removing write/liveness
    // placement never removes it. Every ownership-changing CAS tests its absence.
    private string ServingGrantKey(LogicalShardId shard) => _options.KeyPrefix + "query-serving/" +
        Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(shard.Value));

    private void ObserveServingBarrierOnFailure(TxnRequest request, LogicalShardId shard) =>
        request.Failure.Add(new RequestOp { RequestRange = new() { Key = ByteString.CopyFromUtf8(ServingGrantKey(shard)) } });

    private static void RequireNoServingBarrierAtFailure(TxnResponse response, LogicalShardId shard)
    {
        // Inspect the barrier at the failed CAS's own linearization point. A
        // separate read can race expiry and incorrectly classify a blocked
        // cutover as stale placement, preventing an otherwise valid exact retry.
        if (response.Responses.Count != 1 || response.Responses[0].ResponseRange is not { } range)
        {
            throw new InvalidDataException("Missing atomic serving-barrier observation.");
        }

        if (range.Kvs.Count != 0)
        {
            throw new ShardServingGrantActiveException(shard);
        }
    }

    internal Action? AfterQueryServingPublication
    {
        get; set;
    }

    internal Func<ValueTask>? AfterQueryServingPublicationAsync
    {
        get; set;
    }

    internal Func<ValueTask>? AfterTransferCompareFailure
    {
        get; set;
    }

    internal async ValueTask<PartitionServingGrant[]> AcquireQueryServingGrantsAsync(
        PartitionQueryAuthorityBinding binding, IReadOnlyList<ShardPlacementClaim> claims,
        IDatabaseBoundedClock clock, TimeSpan lifetime, TimeSpan renewalMargin, CancellationToken token)
    {
        if (binding.Namespace != _options.KeyPrefix || binding.Cluster == 0 || clock.Domain == Guid.Empty ||
            claims.Count is < 1 or > MaximumQueryClaims || claims.Select(c => c.Shard).Distinct().Count() != claims.Count ||
            lifetime <= renewalMargin || renewalMargin <= TimeSpan.Zero)
        {
            throw new InvalidDataException("Invalid serving-grant authority or claim set.");
        }

        Guid clockDomain = clock.Domain;
        long ttl = Seconds(lifetime);
        for (int attempt = 0; attempt < _options.MaximumCompareRetries; attempt++)
        {
            var observe = new TxnRequest();
            foreach (var claim in claims)
            {
                observe.Success.Add(new RequestOp { RequestRange = new() { Key = ByteString.CopyFromUtf8(PlacementKey(claim.Shard)) } });
                observe.Success.Add(new RequestOp { RequestRange = new() { Key = ByteString.CopyFromUtf8(ServingGrantKey(claim.Shard)) } });
            }
            var read = await ExecuteAsync((client, cancellation) => client.TransactionAsync(observe, cancellationToken: cancellation), token);
            if (read.Header?.ClusterId != binding.Cluster || !read.Succeeded || read.Responses.Count != claims.Count * 2)
            {
                throw new InvalidDataException("Serving grant observation belongs to another authority.");
            }

            var before = ReadServingClock(clock, clockDomain);
            var grants = new PartitionServingGrant[claims.Count];
            var publish = new TxnRequest();
            var replacements = new List<int>();
            for (int i = 0; i < claims.Count; i++)
            {
                var placement = read.Responses[i * 2].ResponseRange.Kvs;
                if (placement.Count != 1 || placement[0].Lease == 0 ||
                    placement[0].Key != observe.Success[i * 2].RequestRange.Key ||
                    JsonSerializer.Deserialize<PlacementRecord>(placement[0].Value.Span)?.ToPlacement().Claim != claims[i])
                {
                    throw new StaleShardPlacementException(claims[i].Shard);
                }

                var existing = read.Responses[i * 2 + 1].ResponseRange.Kvs;
                PartitionServingGrant? prior = null;
                if (existing.Count != 0)
                {
                    if (existing.Count != 1 || existing[0].Lease == 0 || existing[0].Key != observe.Success[i * 2 + 1].RequestRange.Key)
                    {
                        throw new InvalidDataException("Serving grant is not the expected lease-backed key.");
                    }

                    prior = JsonSerializer.Deserialize<PartitionServingGrant>(existing[0].Value.Span)
                        ?? throw new InvalidDataException("Missing serving grant.");
                    if (prior.Claim != claims[i] || prior.ClockDomain != clockDomain || prior.UntilUtcTicks <= DateTime.MinValue.Ticks || prior.UntilUtcTicks > DateTime.MaxValue.Ticks)
                    {
                        throw new InvalidDataException("Serving grant claim or clock interpretation differs.");
                    }

                    if (before.LatestUtcTicks < prior.UntilUtcTicks - renewalMargin.Ticks)
                    {
                        grants[i] = prior;
                        continue;
                    }
                }
                // The interval is read BEFORE granting the etcd lease. Its lower
                // endpoint plus TTL is no later than that lease's earliest expiry.
                long until = checked(before.EarliestUtcTicks + TimeSpan.FromSeconds(ttl).Ticks);
                if (until > DateTime.MaxValue.Ticks || before.LatestUtcTicks >= until - renewalMargin.Ticks ||
                    prior is not null && until < prior.UntilUtcTicks)
                {
                    throw new InvalidOperationException("Clock uncertainty prevents a safe serving grant.");
                }

                grants[i] = new(claims[i], clockDomain, until);
                replacements.Add(i);
                publish.Compare.Add(ModRevisionCompare(PlacementKey(claims[i].Shard), placement[0].ModRevision));
                publish.Compare.Add(existing.Count == 0 ? VersionCompare(ServingGrantKey(claims[i].Shard), 0) :
                    ModRevisionCompare(ServingGrantKey(claims[i].Shard), existing[0].ModRevision));
            }
            if (replacements.Count == 0)
            {
                return grants;
            }

            long lease = await GrantAsync(ttl, token);
            bool mayHavePublished = false;
            try
            {
                foreach (int i in replacements)
                {
                    publish.Success.Add(Put(ServingGrantKey(claims[i].Shard), JsonSerializer.Serialize(grants[i]), lease));
                }

                mayHavePublished = true;
                var result = await ExecuteAsync((client, cancellation) => client.TransactionAsync(publish, cancellationToken: cancellation), token);
                if (result.Header?.ClusterId != binding.Cluster)
                {
                    throw new InvalidDataException("Serving-grant publication authority changed.");
                }

                if (!result.Succeeded)
                {
                    continue;
                }

                AfterQueryServingPublication?.Invoke();
                if (AfterQueryServingPublicationAsync is { } afterPublication)
                {
                    await afterPublication();
                }
                // A delayed reply never extends the certificate's original deadline.
                if (ReadServingClock(clock, clockDomain).LatestUtcTicks >= grants.Min(g => g.UntilUtcTicks))
                {
                    throw new InvalidOperationException("Serving-grant reply arrived after safe expiry.");
                }

                return grants;
            }
            finally
            {
                // Never revoke a published or possibly published read grant:
                // another home may already have obtained its certificate.
                if (!mayHavePublished)
                {
                    await BestEffortRevokeAsync(lease);
                }
            }
        }
        throw new ShardPlacementContentionException("Read-serving grants changed too frequently.");
    }

    internal static DatabaseClockInterval ReadServingClock(IDatabaseBoundedClock clock, Guid? expectedDomain = null)
    {
        Guid domain = clock.Domain;
        if (domain == Guid.Empty || expectedDomain is { } expected && domain != expected)
        {
            throw new InvalidOperationException("Serving clock domain changed.");
        }

        var value = clock.Read();
        if (clock.Domain != domain)
        {
            throw new InvalidOperationException("Serving clock domain changed during observation.");
        }

        if (value.EarliestUtcTicks < DateTime.MinValue.Ticks || value.LatestUtcTicks < value.EarliestUtcTicks ||
            value.LatestUtcTicks > DateTime.MaxValue.Ticks)
        {
            throw new InvalidOperationException("Invalid bounded clock observation.");
        }

        return value;
    }
}
