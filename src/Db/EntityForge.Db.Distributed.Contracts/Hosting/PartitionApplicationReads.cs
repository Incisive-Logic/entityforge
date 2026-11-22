using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>The extent of the certified read; positions alone are not a certificate for later queries.</summary>
public enum PartitionReadConsistency
{
    /// <summary>The result is certified against a complete distributed read cut.</summary>
    CompleteCut,
    /// <summary>Consistency is certified only for the requested projection.</summary>
    Projection
}

/// <summary>A fully completed consistent result and its certified minimum partition positions.</summary>
/// <remarks>Projection consistency applies only to the requested data. Its position vector
/// may be used as a minimum position, but does not certify an unrestricted global cut.</remarks>
/// <param name="Value">The completed result certified by this read.</param>
/// <param name="Position">The minimum partition positions reached by the completed read.</param>
/// <param name="Consistency">Whether the certificate covers a complete cut or only the requested projection.</param>
public sealed record PartitionReadResult<T>(T Value, DatabasePositionVector Position, PartitionReadConsistency Consistency = PartitionReadConsistency.CompleteCut);
