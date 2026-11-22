namespace EntityForge.Db.Distributed.Sharding;

/// <summary>Exposes the stable binding of a placement authority to its cluster and namespace.</summary>
public interface IShardPlacementAuthorityIdentity
{
    /// <summary>Gets the identity that binds this authority configuration.</summary>
    Guid BindingIdentity
    {
        get;
    }

    /// <summary>Gets the etcd cluster identity expected by this authority.</summary>
    string ClusterIdentity
    {
        get;
    }

    /// <summary>Gets the placement key namespace governed by this authority.</summary>
    string PlacementNamespace
    {
        get;
    }
}
