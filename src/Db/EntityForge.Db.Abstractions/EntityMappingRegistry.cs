using System.Security.Cryptography;
using System.Text.Json;

namespace EntityForge.Db.Mapping;

/// <summary>A durable, explicit mapping registration or compare-and-set compatible upgrade.</summary>
/// <param name="Manifest">Mapping definition to register.</param>
/// <param name="ExpectedFingerprint">Optional expected mapping fingerprint used to guard registration.</param>
public sealed record EntityMappingRegistration(EntityMappingManifest Manifest, string? ExpectedFingerprint = null);

/// <summary>The proposed map does not match the established persistent interpretation.</summary>
public sealed class EntityMappingMismatchException(string message) : InvalidOperationException(message);

internal interface IEntityMappingReplayHost
{
    Guid DatabaseIncarnation
    {
        get;
    }

    ValueTask<DatabaseCommitReceipt?> ResolveMappingReplayAsync(Guid operationId,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
}
