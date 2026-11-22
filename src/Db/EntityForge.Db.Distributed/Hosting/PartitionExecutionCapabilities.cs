using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionApplicationClient
{
    internal PartitionExecutionCapabilities ExecutionCapabilities => new("partition-position-v2", Generation, Profile,
        _routing.Fingerprint, _authority is PartitionNativeHomeAuthority ? "native-home" : "authority-decisions",
        "bounded-clock-mvcc", true, true, true, true,
        Profile is DatabaseProfileId.PropertyGraph or DatabaseProfileId.Rdf, _routing.StorageLayout, _routing.GraphTopologyOwner);
}
