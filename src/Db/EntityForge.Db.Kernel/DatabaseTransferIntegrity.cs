using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EntityForge.Db.Kernel;

/// <summary>Computes the ordered integrity hashes for a database transfer window.</summary>
/// <remarks>Compare the resulting chain and sequence against a trusted expected window. These unkeyed hashes do not authenticate a sender or independently prove that all required commits were supplied.</remarks>
public static class DatabaseTransferIntegrity
{
    /// <summary>The length, in bytes, of each SHA-256 content or chain hash.</summary>
    public const int HashSize = 32;

    /// <summary>Creates the initial chain hash for a database and exclusive lower boundary.</summary>
    /// <param name="databaseIncarnation">Identity of the source database.</param>
    /// <param name="afterExclusive">The version immediately defining the export's excluded lower boundary.</param>
    /// <returns>A new 32-byte seed hash.</returns>
    public static byte[] CreateSeed(Guid databaseIncarnation, DatabaseVersion afterExclusive)
    {
        Span<byte> input = stackalloc byte[24];
        databaseIncarnation.TryWriteBytes(input);
        BinaryPrimitives.WriteInt64LittleEndian(input[16..], afterExclusive.Value);
        return SHA256.HashData(input);
    }

    /// <summary>Computes the next chain hash from the preceding hash and one commit's metadata.</summary>
    /// <param name="previousHash">The 32-byte seed or preceding entry hash.</param>
    /// <param name="sourceSequence">Positive position of this entry within the transfer window.</param>
    /// <param name="version">Authoritative database version of the commit.</param>
    /// <param name="transactionId">Identity of the source transaction.</param>
    /// <param name="profile">Profile that interprets the transaction.</param>
    /// <param name="codecId">Identifier of the transaction payload codec.</param>
    /// <param name="codecVersion">Version of that codec.</param>
    /// <param name="contentHash">The 32-byte SHA-256 digest of the encoded payload.</param>
    /// <param name="storageLayout">Layout fingerprint to include, or null for the current layout.</param>
    /// <returns>A new 32-byte chain hash; input buffers are not modified.</returns>
    /// <exception cref="ArgumentException">An input hash is not 32 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The sequence number is not positive.</exception>
    /// <remarks>This operation does not read the payload or validate sequence continuity. The caller supplies a verified content hash and enforces the expected sequence.</remarks>
    public static byte[] Advance(ReadOnlySpan<byte> previousHash, long sourceSequence,
        DatabaseVersion version, Guid transactionId, DatabaseProfileId profile,
        string codecId, int codecVersion, ReadOnlySpan<byte> contentHash, DatabaseStorageLayout? storageLayout = null)
    {
        if (previousHash.Length != HashSize)
        {
            throw new ArgumentException("A SHA-256 chain hash is required.", nameof(previousHash));
        }

        if (sourceSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSequence));
        }

        if (contentHash.Length != HashSize)
        {
            throw new ArgumentException("A SHA-256 content hash is required.", nameof(contentHash));
        }

        byte[] codecBytes = Encoding.UTF8.GetBytes(codecId);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendLengthPrefixed(hash, Encoding.UTF8.GetBytes((storageLayout ?? DatabaseStorageLayout.Current).Fingerprint));
        hash.AppendData(previousHash);
        Span<byte> scalar = stackalloc byte[20];
        BinaryPrimitives.WriteInt64LittleEndian(scalar, sourceSequence);
        BinaryPrimitives.WriteInt64LittleEndian(scalar[8..], version.Value);
        BinaryPrimitives.WriteInt32LittleEndian(scalar[16..], codecVersion);
        hash.AppendData(scalar);
        Span<byte> transaction = stackalloc byte[16];
        transactionId.TryWriteBytes(transaction);
        hash.AppendData(transaction);
        hash.AppendData([(byte)profile]);
        AppendLengthPrefixed(hash, codecBytes);
        hash.AppendData(contentHash);
        return hash.GetHashAndReset();
    }

    private static void AppendLengthPrefixed(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}
