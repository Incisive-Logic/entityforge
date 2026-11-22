using System.Buffers.Binary;
using EntityForge.Db;

namespace EntityForge.Db.Kernel;

/// <summary>Encodes local committed-change positions and validates them against a database incarnation and durable upper bound.</summary>
public interface IDatabaseChangeCursorCodec
{
    /// <summary>Serializes a version together with the identity of its database incarnation.</summary>
    /// <param name="databaseIncarnation">Nonempty identity of the database that issued the position.</param>
    /// <param name="version">Version to serialize; a resumable change-feed position must be positive.</param>
    /// <returns>An opaque cursor for storage and later resumption.</returns>
    /// <exception cref="ArgumentException">The database incarnation is empty.</exception>
    DatabaseChangeCursor Encode(Guid databaseIncarnation, DatabaseVersion version);

    /// <summary>Validates a cursor's contract, incarnation and position against the durable database version.</summary>
    /// <param name="cursor">An initialized cursor obtained from the change feed.</param>
    /// <param name="expectedDatabaseIncarnation">The authoritative identity of the database serving the request.</param>
    /// <param name="durableVersion">Inclusive upper bound on accepted cursor versions.</param>
    /// <returns>The positive local version represented by the cursor.</returns>
    /// <exception cref="EntityForgeChangeCursorInvalidException">The encoded contract or incarnation is invalid,
    /// or the version is zero or beyond the durable upper bound.</exception>
    /// <remarks>Decoding does not check the retained-history floor. The feed reader must separately
    /// determine whether the requested history is still available.</remarks>
    DatabaseVersion Decode(DatabaseChangeCursor cursor, Guid expectedDatabaseIncarnation,
        DatabaseVersion durableVersion);
}

/// <summary>Implements the versioned, incarnation-bound cursor format for a local committed-change feed.</summary>
/// <remarks>Cursors are encoded, not cryptographically authenticated. Validation is not authorization;
/// callers must enforce access control and retained-history checks independently.</remarks>
public sealed class DatabaseChangeCursorCodec : IDatabaseChangeCursorCodec
{
    private static ReadOnlySpan<byte> Magic => "EFCC"u8;

    private const byte CursorCodecVersion = 1;
    private const int FeedContractVersion = 1;
    private const byte LocalVersionPositionKind = 1;
    private const int MagicBytes = 4;
    private const int CodecVersionBytes = sizeof(byte);
    private const int FeedVersionBytes = sizeof(int);
    private const int PositionKindBytes = sizeof(byte);
    private const int IncarnationBytes = 16;
    private const int VersionBytes = sizeof(long);
    private const int IncarnationOffset = MagicBytes + CodecVersionBytes + FeedVersionBytes + PositionKindBytes;
    private const int VersionOffset = IncarnationOffset + IncarnationBytes;
    private const int CursorBytes = VersionOffset + VersionBytes;

    /// <inheritdoc/>
    /// <remarks>This method serializes the supplied version; positive-version and durable-bound checks occur on decode.</remarks>
    public DatabaseChangeCursor Encode(Guid databaseIncarnation, DatabaseVersion version)
    {
        if (databaseIncarnation == Guid.Empty)
        {
            throw new ArgumentException("A database incarnation is required.", nameof(databaseIncarnation));
        }

        Span<byte> bytes = stackalloc byte[CursorBytes];
        Magic.CopyTo(bytes);
        bytes[MagicBytes] = CursorCodecVersion;
        BinaryPrimitives.WriteInt32BigEndian(bytes[(MagicBytes + CodecVersionBytes)..], FeedContractVersion);
        bytes[MagicBytes + CodecVersionBytes + FeedVersionBytes] = LocalVersionPositionKind;
        databaseIncarnation.TryWriteBytes(bytes[IncarnationOffset..]);
        BinaryPrimitives.WriteInt64BigEndian(bytes[VersionOffset..], version.Value);
        return new(Convert.ToBase64String(bytes));
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">The cursor is an uninitialized default value with no encoded text.</exception>
    public DatabaseVersion Decode(DatabaseChangeCursor cursor, Guid expectedDatabaseIncarnation,
        DatabaseVersion durableVersion)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(cursor.Value);
        }
        catch (FormatException exception)
        {
            throw new EntityForgeChangeCursorInvalidException(exception.Message);
        }
        if (bytes.Length != CursorBytes || !bytes.AsSpan(0, MagicBytes).SequenceEqual(Magic) ||
            bytes[MagicBytes] != CursorCodecVersion ||
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(MagicBytes + CodecVersionBytes)) != FeedContractVersion ||
            bytes[MagicBytes + CodecVersionBytes + FeedVersionBytes] != LocalVersionPositionKind)
        {
            throw new EntityForgeChangeCursorInvalidException(
                "The change cursor uses an unsupported cursor or feed contract.");
        }

        if (new Guid(bytes.AsSpan(IncarnationOffset, IncarnationBytes)) != expectedDatabaseIncarnation)
        {
            throw new EntityForgeChangeCursorInvalidException(
                "The change cursor does not belong to this database incarnation.");
        }

        long version = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(VersionOffset, VersionBytes));
        if (version <= DatabaseVersion.Initial.Value || version > durableVersion.Value)
        {
            throw new EntityForgeChangeCursorInvalidException(
                "The change cursor version is outside the durable database history.");
        }

        return new(version);
    }
}
