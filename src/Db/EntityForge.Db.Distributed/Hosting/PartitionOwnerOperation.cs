using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// Prototype UUIDv8 namespace, with the same time field and random payload as
// UUIDv7. Routing the immutable identity chooses its sole execution owner.
// A generation's routing remains fixed; role-preserving transfer is allowed.
internal static class PartitionOwnerOperation
{
    internal const string Policy = "entityforge-owner-v1";
    private const int AttemptsPerShard = 128;

    internal static bool IsOwnerLocal(Guid operation)
    {
        Span<byte> bytes = stackalloc byte[16];
        operation.TryWriteBytes(bytes, bigEndian: true, out _);
        return (bytes[6] >> 4) == 8;
    }

    internal static Guid New(DistributedRoutingBinding routing, LogicalShardId owner, DateTimeOffset? createdAt = null)
    {
        if (!routing.Shards.Contains(owner))
        {
            throw new ArgumentException("Unknown operation owner.");
        }

        Span<byte> bytes = stackalloc byte[16];
        int maximumAttempts = checked(AttemptsPerShard * routing.Shards.Count);
        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            EntityOperationIds.New(createdAt ?? DateTimeOffset.UtcNow).TryWriteBytes(bytes, bigEndian: true, out _);
            bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80);
            var operation = new Guid(bytes, bigEndian: true);
            if (RouteHome(operation, routing) == owner)
            {
                return operation;
            }
        }
        throw new InvalidOperationException("Unable to allocate an owner identity within the bounded sampling budget.");
    }

    internal static LogicalShardId Owner(Guid operation, DistributedRoutingBinding routing)
    {
        if (!IsOwnerLocal(operation) || !EntityOperationIds.TryGetCreatedAt(operation, out _))
        {
            throw new InvalidDataException("Not an owner-addressed operation.");
        }

        return RouteHome(operation, routing);
    }

    // Policy v1 freezes this hash domain independently of future vertex-routing changes.
    private static LogicalShardId RouteHome(Guid operation, DistributedRoutingBinding routing) =>
        routing.StorageLayout == DatabaseStorageLayout.Current
            ? routing.Route(DistributedDatabaseProfile.Graph, operation.ToString("N"))
            : throw new NotSupportedException("Unsupported owner identity storage layout.");

    internal static void RequireCoordinatedNamespace(Guid operation)
    {
        if (IsOwnerLocal(operation))
        {
            throw new InvalidDataException("Owner-addressed operations cannot enter central arbitration.");
        }
    }
}
