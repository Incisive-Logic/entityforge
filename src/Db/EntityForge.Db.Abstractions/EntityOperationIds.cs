using System.Security.Cryptography;

namespace EntityForge.Db;

/// <summary>Creates time-bearing operation identities suitable for an enforced retry horizon.</summary>
/// <remarks>
/// Retention uses the millisecond creation time encoded in a UUIDv7, not the first submission time.
/// An ID exactly at the configured cutoff remains eligible; earlier IDs and identities without the supported time layout
/// are rejected as expired once a positive cutoff is installed. Use a UTC clock synchronized with
/// the receiving service, which can reject future-dated admission. Preserve the ID across retries;
/// replacing an expired ID creates a different logical operation and can repeat its effects.
/// </remarks>
public static class EntityOperationIds
{
    internal static readonly TimeSpan MaximumOwnerFutureSkew = TimeSpan.FromMinutes(5);

    internal static void RequireOwnerAdmissionTime(Guid operation)
    {
        if (IsOwnerAddressed(operation) && (!TryGetCreatedAt(operation, out var createdAt) ||
            createdAt > DateTimeOffset.UtcNow + MaximumOwnerFutureSkew))
        {
            throw new ArgumentException("Owner identity has an invalid timestamp or exceeds the five-minute future-skew allowance.", nameof(operation));
        }
    }

    internal static bool IsOwnerAddressed(Guid operation)
    {
        Span<byte> bytes = stackalloc byte[16];
        operation.TryWriteBytes(bytes, bigEndian: true, out _);
        return bytes[6] >> 4 == 8;
    }

    /// <summary>Creates a UUIDv7 operation identity using the current UTC time.</summary>
    /// <returns>A new identity to retain unchanged for every retry of the same operation.</returns>
    public static Guid New() => New(DateTimeOffset.UtcNow);

    /// <summary>Creates a UUIDv7 identity at the supplied operation creation time. Preserve this ID across retries.</summary>
    /// <param name="createdAt">The creation instant, encoded at millisecond precision.</param>
    /// <returns>A UUIDv7 containing the creation time and fresh random bits.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The time precedes the Unix epoch or exceeds the timestamp field.</exception>
    public static Guid New(DateTimeOffset createdAt)
    {
        long milliseconds = createdAt.ToUnixTimeMilliseconds();
        if (milliseconds < 0 || milliseconds > 0xFFFFFFFFFFFFL)
        {
            throw new ArgumentOutOfRangeException(nameof(createdAt));
        }

        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        for (int index = 5; index >= 0; index--)
        {
            bytes[index] = (byte)milliseconds;
            milliseconds >>= 8;
        }
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>Reads the millisecond UTC timestamp from UUIDv7 or the reserved partition-owner UUIDv8 layout.</summary>
    /// <param name="operation">The operation identity to inspect.</param>
    /// <param name="createdAt">The decoded UTC instant, or the default value when decoding fails.</param>
    /// <returns>True if the supported version, variant and representable timestamp are present.</returns>
    /// <remarks>Decoding does not establish admission, retention or whether the operation committed.</remarks>
    public static bool TryGetCreatedAt(Guid operation, out DateTimeOffset createdAt)
    {
        Span<byte> bytes = stackalloc byte[16];
        operation.TryWriteBytes(bytes, bigEndian: true, out _);
        // UUIDv8 is reserved by the partition-owner prototype with the same
        // timestamp layout. Its retry horizon must not reset on native delivery.
        if ((bytes[6] >> 4) is not (7 or 8) || (bytes[8] & 0xc0) != 0x80)
        {
            createdAt = default;
            return false;
        }
        long milliseconds = 0;
        for (int index = 0; index < 6; index++)
        {
            milliseconds = (milliseconds << 8) | bytes[index];
        }

        if (milliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            createdAt = default;
            return false;
        }
        createdAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        return true;
    }

    internal static bool IsBefore(Guid operation, long retainedSinceMilliseconds) => retainedSinceMilliseconds > 0 &&
        (!TryGetCreatedAt(operation, out var createdAt) || createdAt.ToUnixTimeMilliseconds() < retainedSinceMilliseconds);

    internal static void RequireRetained(Guid operation, long retainedSinceMilliseconds)
    {
        if (IsBefore(operation, retainedSinceMilliseconds))
        {
            throw new EntityOperationExpiredException(operation);
        }
    }
}

/// <summary>The retry service no longer retains this identity. This says nothing about its former commit decision.</summary>
/// <param name="operationId">The expired identity whose former outcome is unavailable.</param>
public sealed class EntityOperationExpiredException(Guid operationId)
    : InvalidOperationException("The operation lies outside the retained retry horizon; its previous outcome is unavailable.")
{
    /// <summary>Gets the expired identity; resubmitting with a new identity can repeat previously committed effects.</summary>
    public Guid OperationId { get; } = operationId;
}
