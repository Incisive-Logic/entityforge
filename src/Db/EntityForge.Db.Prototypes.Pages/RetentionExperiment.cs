using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace EntityForge.Db.Prototypes.Pages;

public enum RetentionProfileKind
{
    Graph = 0, Semantic = 1
}

public enum RetainedVersionKind
{
    QueryableVersion = 0, EvidenceHistory = 1
}

public sealed record RetainedVersion(long EntityId, long BeginVersion, long? EndVersion,
    string Value, string? EvidenceReference, RetainedVersionKind Kind);

public sealed record RetentionExperimentOptions(int EntityCount, int VersionsPerEntity, int SnapshotRound)
{
    public static RetentionExperimentOptions Default { get; } = new(10_000, 5, 3);
}

public sealed record RetentionProfileResult(RetentionProfileKind Profile, int OriginalVersionCount,
    int RetainedWithSnapshot, int RetainedAfterSnapshotRelease, int EvidenceHistoryCount,
    long CheckpointBytes, string LogicalDigest);

public sealed record RetentionExperimentResult(int SchemaVersion, long SnapshotFloor,
    IReadOnlyList<RetentionProfileResult> Results)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public sealed class RetentionExperiment
{
    public const int ResultSchemaVersion = 1;
    private const uint CheckpointMagic = 0x45465248;
    private const int CheckpointHeaderSize = 16;
    private const int ChecksumSize = 4;

    public async Task<RetentionExperimentResult> RunAsync(RetentionExperimentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= RetentionExperimentOptions.Default;
        Validate(options);
        RetainedVersion[] source = Versions(options);
        long snapshotFloor = (long)options.SnapshotRound * options.EntityCount;
        long latestVersion = (long)options.VersionsPerEntity * options.EntityCount;
        var results = new List<RetentionProfileResult>();
        foreach (RetentionProfileKind profile in Enum.GetValues<RetentionProfileKind>())
        {
            RetainedVersion[] withSnapshot = Reclaim(source, snapshotFloor, profile);
            RetainedVersion[] afterRelease = Reclaim(source, latestVersion, profile);
            byte[] checkpoint = EncodeCheckpoint(afterRelease);
            string path = Path.Combine(Path.GetTempPath(), $"entityforge-retention-{Guid.NewGuid():N}.checkpoint");
            try
            {
                await File.WriteAllBytesAsync(path, checkpoint, cancellationToken).ConfigureAwait(false);
                RetainedVersion[] recovered = DecodeCheckpoint(
                    await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
                string expected = Digest(afterRelease);
                string actual = Digest(recovered);
                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                {
                    throw new PrototypePageCorruptionException("Retention checkpoint changed the logical digest.");
                }

                results.Add(new(profile, source.Length, withSnapshot.Length, afterRelease.Length,
                    afterRelease.Count(item => item.Kind == RetainedVersionKind.EvidenceHistory),
                    checkpoint.Length, actual));
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        return new(ResultSchemaVersion, snapshotFloor, results);
    }

    public static RetainedVersion[] Reclaim(IEnumerable<RetainedVersion> source, long oldestSnapshotVersion,
        RetentionProfileKind profile)
    {
        if (oldestSnapshotVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(oldestSnapshotVersion));
        }

        return source.Select(item =>
            {
                bool remainsQueryable = item.EndVersion is null || item.EndVersion > oldestSnapshotVersion;
                if (remainsQueryable)
                {
                    return item with
                    {
                        Kind = RetainedVersionKind.QueryableVersion
                    };
                }

                return profile == RetentionProfileKind.Semantic
                    ? item with
                    {
                        Kind = RetainedVersionKind.EvidenceHistory
                    }
                    : null;
            })
            .Where(item => item is not null).Cast<RetainedVersion>()
            .OrderBy(item => item.EntityId).ThenBy(item => item.BeginVersion).ToArray();
    }

    public static string Digest(IEnumerable<RetainedVersion> versions)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(versions.OrderBy(item => item.EntityId)
            .ThenBy(item => item.BeginVersion));
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private static byte[] EncodeCheckpoint(IReadOnlyList<RetainedVersion> versions)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(versions);
        var checkpoint = new byte[CheckpointHeaderSize + payload.Length + ChecksumSize];
        BinaryPrimitives.WriteUInt32LittleEndian(checkpoint, CheckpointMagic);
        BinaryPrimitives.WriteInt32LittleEndian(checkpoint.AsSpan(4), ResultSchemaVersion);
        BinaryPrimitives.WriteInt64LittleEndian(checkpoint.AsSpan(8), payload.Length);
        payload.CopyTo(checkpoint, CheckpointHeaderSize);
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(
            SHA256.HashData(checkpoint.AsSpan(0, checkpoint.Length - ChecksumSize)));
        BinaryPrimitives.WriteUInt32LittleEndian(checkpoint.AsSpan(checkpoint.Length - ChecksumSize), checksum);
        return checkpoint;
    }

    private static RetainedVersion[] DecodeCheckpoint(byte[] checkpoint)
    {
        if (checkpoint.Length < CheckpointHeaderSize + ChecksumSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(checkpoint) != CheckpointMagic ||
            BinaryPrimitives.ReadInt32LittleEndian(checkpoint.AsSpan(4)) != ResultSchemaVersion)
        {
            throw new PrototypePageCorruptionException("The retention checkpoint header is invalid.");
        }

        long payloadLength = BinaryPrimitives.ReadInt64LittleEndian(checkpoint.AsSpan(8));
        if (payloadLength < 0 || payloadLength != checkpoint.Length - CheckpointHeaderSize - ChecksumSize)
        {
            throw new PrototypePageCorruptionException("The retention checkpoint length is invalid.");
        }

        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(checkpoint.AsSpan(checkpoint.Length - ChecksumSize));
        uint actual = BinaryPrimitives.ReadUInt32LittleEndian(
            SHA256.HashData(checkpoint.AsSpan(0, checkpoint.Length - ChecksumSize)));
        if (expected != actual)
        {
            throw new PrototypePageCorruptionException("The retention checkpoint checksum is invalid.");
        }

        return JsonSerializer.Deserialize<RetainedVersion[]>(checkpoint.AsSpan(CheckpointHeaderSize,
            checked((int)payloadLength))) ?? throw new PrototypePageCorruptionException(
            "The retention checkpoint has no version payload.");
    }

    private static RetainedVersion[] Versions(RetentionExperimentOptions options)
    {
        var versions = new List<RetainedVersion>(options.EntityCount * options.VersionsPerEntity);
        for (int entity = 0; entity < options.EntityCount; entity++)
        {
            for (int round = 0; round < options.VersionsPerEntity; round++)
            {
                long begin = (long)round * options.EntityCount + entity + 1;
                long? end = round == options.VersionsPerEntity - 1
                    ? null : (long)(round + 1) * options.EntityCount + entity + 1;
                versions.Add(new(entity, begin, end, $"value-{round}",
                    $"prov:source/{entity}/activity/{round}", RetainedVersionKind.QueryableVersion));
            }
        }

        return versions.ToArray();
    }

    private static void Validate(RetentionExperimentOptions options)
    {
        if (options.EntityCount <= 0 || options.VersionsPerEntity <= 1 || options.SnapshotRound <= 0 ||
            options.SnapshotRound >= options.VersionsPerEntity)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }
}
