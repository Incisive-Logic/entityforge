using System.Security.Cryptography;
using System.Text.Json;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Distributed.Hosting;

// Creation/transfer evidence, not a second transaction journal. Every established
// node requires this file and its native recovery set before it can renew a lease.
internal sealed record PartitionNodeMetadata(int Format, Guid Generation, string Partition,
    string Routing, DatabaseProfileId Profile, Guid Incarnation, long MapVersion,
    string Owner, long Fence, long MinimumVersion = 0, string? TransactionAuthority = null, string? ServingAuthority = null, string? TimestampAuthority = null, DatabaseStorageLayout? Layout = null, int? TopologyPropertySlot = null, int? PropertyOwnerCount = null, int? SemanticParticipationVersion = null)
{
    // Format 1 admitted local operations without shared application arbitration.
    // Such an identity cannot safely be reused at a different owner after opening
    // under automatic routing. This unreleased format deliberately fails closed.
    // Format 3 reserves UUIDv8 for immutable owner addressing. Older enrollment
    // cannot establish that those identities were excluded from central binding.
    // Format 4 pins serving ownership semantics and its durable authority domain.
    // Formats 7/8 pin evidence-backed single-owner native decisions. Earlier
    // enrollment must not reopen a journal under a different decision protocol.
    internal const int CurrentFormat = 7;
    internal const int CurrentSemanticFormat = 10;
    // Format 10 / participation 2 pins co-located mapped sources and policy fences on
    // every owner. Older enrollments require explicit import into a fresh domain.
    internal const int CurrentSemanticParticipationVersion = 2;

    internal PartitionExecutionDomain Domain => new(Generation, new(Partition), Routing, Profile, Incarnation);

    internal ShardPlacementClaim Claim => new(new(Partition), new(Owner), new(Generation), new(MapVersion), new(Fence));

    internal static PartitionNodeMetadata Create(PartitionExecutionDomain domain, ShardPlacementClaim claim) =>
        new(domain.Profile == DatabaseProfileId.Semantic ? CurrentSemanticFormat : CurrentFormat, domain.Generation, domain.Partition.Value, domain.RoutingFingerprint, domain.Profile,
            domain.Incarnation, claim.MapVersion.Value, claim.Owner.Value, claim.FencingToken.Value,
            SemanticParticipationVersion: domain.Profile == DatabaseProfileId.Semantic ? CurrentSemanticParticipationVersion : null);

    internal void Require(ShardPlacementClaim claim, DistributedRoutingBinding routing)
    {
        _ = Domain;
        if (Profile == DatabaseProfileId.Semantic && SemanticParticipationVersion != CurrentSemanticParticipationVersion ||
            Format != (Profile == DatabaseProfileId.Semantic ? CurrentSemanticFormat : CurrentFormat) || Claim != claim || routing.MapVersion != claim.MapVersion ||
            Routing != routing.Fingerprint || !routing.Shards.Contains(claim.Shard) ||
            routing.StorageLayout != (Layout ?? DatabaseStorageLayout.Current) ||
            routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties && (Profile != DatabaseProfileId.PropertyGraph ||
                TopologyPropertySlot != routing.Shards.ToList().IndexOf(claim.Shard) || PropertyOwnerCount != routing.Shards.Count - 1) ||
            routing.StorageLayout == DatabaseStorageLayout.Current && (TopologyPropertySlot is not null || PropertyOwnerCount is not null) || MapVersion <= 0 || Fence <= 0 ||
            string.IsNullOrWhiteSpace(Owner) || MinimumVersion < 0)
        {
            throw new InvalidDataException("Partition enrollment does not match the requested ownership and routing.");
        }
    }
}

internal static class PartitionMetadataFile
{
    private const int MaximumBytes = 64 * 1024;
    private const int DigestBytes = 32;

    internal static async ValueTask<T> ReadAsync<T>(string path, CancellationToken token, int maximumBytes = MaximumBytes)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            MaximumBytes, FileOptions.Asynchronous);
        if (source.Length <= DigestBytes || source.Length > maximumBytes)
        {
            throw new InvalidDataException("Invalid partition metadata length.");
        }

        byte[] bytes = new byte[checked((int)source.Length)];
        await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        int length = bytes.Length - DigestBytes;
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.AsSpan(0, length)), bytes.AsSpan(length)))
        {
            throw new InvalidDataException("Partition metadata checksum mismatch.");
        }

        return JsonSerializer.Deserialize<T>(bytes.AsSpan(0, length)) ?? throw new InvalidDataException("Empty partition metadata.");
    }

    internal static async ValueTask WriteNewAsync<T>(string path, T value, CancellationToken token, int maximumBytes = MaximumBytes)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > maximumBytes - DigestBytes)
        {
            throw new InvalidDataException("Partition metadata exceeds its limit.");
        }

        string temporary = path + ".new";
        // An interrupted create is never silently replaced or adopted by Open.
        await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
            MaximumBytes, FileOptions.Asynchronous))
        {
            await file.WriteAsync(bytes, token).ConfigureAwait(false);
            await file.WriteAsync(SHA256.HashData(bytes), token).ConfigureAwait(false);
            file.Flush(true);
        }
        File.Move(temporary, path, false);
        DurableDirectory.Sync(Path.GetDirectoryName(path)!);
    }
}
