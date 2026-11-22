using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EntityForge.Db.Query.Planning;

/// <summary>Identifies why a query cannot resume from a supplied continuation.</summary>
public enum EntityQueryContinuationFailure
{
    /// <summary>The token's representation, version, checksum or position encoding is invalid.</summary>
    Malformed,

    /// <summary>The token belongs to a different query.</summary>
    QueryMismatch,

    /// <summary>The requested snapshot differs from the token's snapshot.</summary>
    SnapshotMismatch,

    /// <summary>The snapshot required to resume the query is no longer retained.</summary>
    SnapshotExpired,

    /// <summary>The saved position is absent from the retained ordered candidates.</summary>
    PositionMissing
}

/// <summary>Reports an invalid or unusable query continuation.</summary>
/// <param name="failure">The machine-readable failure category.</param>
/// <param name="message">A description of the failed continuation check.</param>
public sealed class EntityQueryContinuationException(
    EntityQueryContinuationFailure failure, string message) : Exception(message)
{
    /// <summary>Gets the continuation failure category.</summary>
    public EntityQueryContinuationFailure Failure { get; } = failure;
}

/// <summary>Associates a paging position with its query fingerprint and snapshot.</summary>
/// <param name="Snapshot">The snapshot version required by the query evaluator.</param>
/// <param name="QueryFingerprint">A hexadecimal SHA-256 query fingerprint.</param>
/// <param name="Position">The evaluator's opaque, stable position in its ordered candidates.</param>
public sealed record EntityQueryContinuation(long Snapshot, string QueryFingerprint, string Position);

/// <summary>Encodes and validates canonical base64url query continuation tokens.</summary>
/// <remarks>The unkeyed checksum detects corruption; it is not an authorization signature or encryption.
/// Callers must separately validate the query fingerprint, snapshot availability and permitted data access.</remarks>
public static class EntityQueryContinuationCodec
{
    private const byte FormatVersion = 1;
    private const int FingerprintSize = 32;
    private const int ChecksumSize = 32;
    private const int HeaderSize = 1 + sizeof(long) + FingerprintSize + sizeof(int);
    private const int MaximumTokenCharacters = 4_096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Encodes a continuation as a versioned, checksummed token of at most 4,096 characters.</summary>
    /// <param name="continuation">Snapshot, SHA-256 fingerprint and valid Unicode position to encode.</param>
    /// <returns>A canonical unpadded base64url token.</returns>
    /// <exception cref="ArgumentNullException">The continuation or one of its required strings is null.</exception>
    /// <exception cref="ArgumentException">The fingerprint has the wrong size, or the position is invalid Unicode or too large.</exception>
    /// <exception cref="FormatException">The fingerprint is not hexadecimal.</exception>
    public static string Encode(EntityQueryContinuation continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        byte[] fingerprint = Convert.FromHexString(continuation.QueryFingerprint);
        if (fingerprint.Length != FingerprintSize)
        {
            throw new ArgumentException("A continuation fingerprint must be a SHA-256 digest.", nameof(continuation));
        }

        int positionSize = StrictUtf8.GetByteCount(continuation.Position);
        if (positionSize > MaximumTokenCharacters / 4 * 3 - HeaderSize - ChecksumSize)
        {
            throw new ArgumentException("The continuation position exceeds the token size limit.", nameof(continuation));
        }
        byte[] position = StrictUtf8.GetBytes(continuation.Position);
        byte[] payload = new byte[HeaderSize + position.Length];
        payload[0] = FormatVersion;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1), continuation.Snapshot);
        fingerprint.CopyTo(payload.AsSpan(1 + sizeof(long)));
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(1 + sizeof(long) + FingerprintSize), position.Length);
        position.CopyTo(payload.AsSpan(HeaderSize));
        byte[] checksum = SHA256.HashData(payload);
        return Base64Url(payload.Concat(checksum).ToArray());
    }

    /// <summary>Validates and decodes a canonical token without resolving its query or snapshot.</summary>
    /// <param name="token">An unpadded base64url token of at most 4,096 characters.</param>
    /// <returns>The decoded continuation, with an uppercase hexadecimal fingerprint.</returns>
    /// <exception cref="EntityQueryContinuationException">The token is malformed, unsupported, corrupt or contains invalid UTF-8.</exception>
    public static EntityQueryContinuation Decode(string token)
    {
        if (String.IsNullOrWhiteSpace(token) || token.Length > MaximumTokenCharacters)
        {
            throw Failure("The continuation token is empty or exceeds its size limit.");
        }

        byte[] content;
        try
        {
            content = Convert.FromBase64String(FromBase64Url(token));
        }
        catch (FormatException)
        {
            throw Failure("The continuation token is not valid base64url.");
        }
        if (!StringComparer.Ordinal.Equals(Base64Url(content), token))
        {
            throw Failure("The continuation token is not canonical base64url.");
        }

        if (content.Length < HeaderSize + ChecksumSize || content[0] != FormatVersion)
        {
            throw Failure("The continuation token format is unsupported or truncated.");
        }

        ReadOnlySpan<byte> payload = content.AsSpan(0, content.Length - ChecksumSize);
        ReadOnlySpan<byte> checksum = content.AsSpan(content.Length - ChecksumSize);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), checksum))
        {
            throw Failure("The continuation token integrity check failed.");
        }

        int positionLength = BinaryPrimitives.ReadInt32BigEndian(
            payload.Slice(1 + sizeof(long) + FingerprintSize, sizeof(int)));
        if (positionLength < 0 || HeaderSize + positionLength != payload.Length)
        {
            throw Failure("The continuation token position bounds are invalid.");
        }

        try
        {
            return new(BinaryPrimitives.ReadInt64BigEndian(payload[1..]),
                Convert.ToHexString(payload.Slice(1 + sizeof(long), FingerprintSize)),
                StrictUtf8.GetString(payload[HeaderSize..]));
        }
        catch (DecoderFallbackException)
        {
            throw Failure("The continuation position is not valid UTF-8.");
        }
    }

    private static EntityQueryContinuationException Failure(string message) =>
        new(EntityQueryContinuationFailure.Malformed, message);

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string FromBase64Url(string value)
    {
        string result = value.Replace('-', '+').Replace('_', '/');
        return result.PadRight(result.Length + ((4 - result.Length % 4) % 4), '=');
    }
}

/// <summary>A materialized page from an already ordered candidate list.</summary>
/// <typeparam name="TCandidate">The candidate element type.</typeparam>
/// <param name="Items">Candidates in the input list's order.</param>
/// <param name="NextPosition">The last returned candidate's position when more remain; otherwise null.</param>
public sealed record EntityCandidatePage<TCandidate>(
    IReadOnlyList<TCandidate> Items, string? NextPosition);

/// <summary>Pages an existing ordered list without acquiring or retaining a database snapshot.</summary>
public static class EntityCandidatePaginator
{
    /// <summary>Copies at most the requested number of candidates after an optional saved position.</summary>
    /// <typeparam name="TCandidate">The candidate element type.</typeparam>
    /// <param name="orderedCandidates">A stable ordered list for the same query snapshot.</param>
    /// <param name="pageSize">Maximum number of candidates to return; must be positive.</param>
    /// <param name="afterPosition">Position to resume after, or null for the first page.</param>
    /// <param name="position">Returns a unique, stable position for each candidate; comparisons are ordinal.</param>
    /// <returns>The page and a position for resuming when more candidates remain.</returns>
    /// <remarks>Resume lookup is linear and uses the first matching position. The caller is responsible
    /// for uniqueness, ordering and snapshot lifetime; this helper does not stream the source list.</remarks>
    /// <exception cref="ArgumentNullException">The candidate list or position function is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page size is not positive.</exception>
    /// <exception cref="EntityQueryContinuationException">The saved position is absent from the list.</exception>
    public static EntityCandidatePage<TCandidate> Page<TCandidate>(
        IReadOnlyList<TCandidate> orderedCandidates, int pageSize, string? afterPosition,
        Func<TCandidate, string> position)
    {
        ArgumentNullException.ThrowIfNull(orderedCandidates);
        ArgumentNullException.ThrowIfNull(position);
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        int start = 0;
        if (afterPosition is not null)
        {
            int found = -1;
            for (int index = 0; index < orderedCandidates.Count; index++)
            {
                if (StringComparer.Ordinal.Equals(position(orderedCandidates[index]), afterPosition))
                {
                    found = index;
                    break;
                }
            }

            if (found < 0)
            {
                throw new EntityQueryContinuationException(EntityQueryContinuationFailure.PositionMissing,
                    "The continuation position is not present in the retained query snapshot.");
            }

            start = found + 1;
        }
        TCandidate[] items = orderedCandidates.Skip(start).Take(pageSize).ToArray();
        bool hasMore = start + items.Length < orderedCandidates.Count;
        return new(items, hasMore && items.Length != 0 ? position(items[^1]) : null);
    }
}
