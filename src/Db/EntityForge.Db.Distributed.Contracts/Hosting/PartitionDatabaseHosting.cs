using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>An exact physical owner identity and its participant HTTPS endpoint.</summary>
/// <param name="Identity">The exact physical partition identity, including generation and profile.</param>
/// <param name="Endpoint">The absolute participant HTTPS endpoint for this owner.</param>
public sealed record PartitionRemoteOwner(DatabasePartitionIdentity Identity, Uri Endpoint);
