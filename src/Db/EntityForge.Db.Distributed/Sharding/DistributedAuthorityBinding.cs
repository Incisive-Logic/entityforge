namespace EntityForge.Db.Distributed.Sharding;

/// <summary>Binds a placement authority to a specific etcd cluster and placement key namespace.</summary>
public sealed class EtcdPlacementAuthorityBinding
{
    internal EtcdPlacementAuthorityBinding(IReadOnlyList<Uri> endpoints, string placementNamespace)
    {
        Identity = Guid.NewGuid();
        ClusterIdentity = DistributedAuthorityIdentity.Cluster(endpoints);
        PlacementNamespace = placementNamespace;
    }

    /// <summary>Gets the stable identifier derived from the cluster and namespace binding.</summary>
    public Guid Identity
    {
        get;
    }

    /// <summary>Gets the etcd cluster identity.</summary>
    public string ClusterIdentity
    {
        get;
    }

    /// <summary>Gets the normalized key namespace for shard placements.</summary>
    public string PlacementNamespace
    {
        get;
    }
}

internal static class DistributedAuthorityIdentity
{
    public static string Cluster(IReadOnlyList<Uri> endpoints) => String.Join('|', endpoints
        .Select(endpoint => endpoint.AbsoluteUri.TrimEnd('/').ToUpperInvariant())
        .OrderBy(endpoint => endpoint, StringComparer.Ordinal));
}
