namespace EntityForge.Db.Distributed.Sharding;

internal sealed record PartitionQueryAuthorityBinding(ulong Cluster, string Namespace);

public sealed partial class GrpcEtcdShardPlacementAuthority
{
    internal const int MaximumQueryClaims = 32;

    internal async ValueTask<PartitionQueryAuthorityBinding> QueryBindingAsync(CancellationToken token)
    {
        var response = await RangeAsync(_options.KeyPrefix + "query-authority", token);
        if (response.Header is null || response.Header.ClusterId == 0)
        {
            throw new InvalidDataException("Missing actual authority identity.");
        }

        return new(response.Header.ClusterId, _options.KeyPrefix);
    }

}
