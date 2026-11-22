using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>The fixed identity interpretation advertised by an enrolled application endpoint.</summary>
/// <param name="Generation">The database incarnation served by the enrolled endpoint.</param>
/// <param name="Profile">The database profile served by the endpoint.</param>
/// <param name="IdentityPolicy">The fixed identity interpretation required for mapped application operations.</param>
/// <param name="Routing">The exact routing policy, shard map and layout binding accepted by this owner.</param>
/// <param name="MaximumFutureSkewSeconds">The maximum accepted future clock skew for application timestamps, in seconds.</param>
public sealed record PartitionOwnerCapabilities(Guid Generation, DatabaseProfileId Profile,
    string IdentityPolicy, DistributedRoutingBinding Routing, int MaximumFutureSkewSeconds);
