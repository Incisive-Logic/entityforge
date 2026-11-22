using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EntityForge.Db.Semantic;

internal enum SemanticAssertionRowKind : byte
{
    Field = 1,
    Relationship = 2
}

internal readonly record struct SemanticAtomId
{
    public const int ByteLength = 16;
    private readonly Guid _value;

    public SemanticAtomId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A semantic atom ID cannot be empty.", nameof(value));
        }

        _value = value;
    }

    public static SemanticAtomId FromCanonicalBytes(ReadOnlySpan<byte> canonicalBytes)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(canonicalBytes, hash);
        return new(new Guid(hash[..ByteLength]));
    }

    public static SemanticAtomId Read(ReadOnlySpan<byte> bytes) => new(new Guid(bytes[..ByteLength]));

    public void Write(Span<byte> destination) => _value.TryWriteBytes(destination);

    public Guid Value => _value;
}

internal sealed record SemanticFieldAssertionRow(Guid AssertionId, SemanticAtomId Subject,
    SemanticAtomId Field, SemanticAtomId Value, SemanticAtomId Provenance,
    DatabaseVersion BeginVersion, DatabaseVersion? EndVersion);

internal sealed record SemanticRelationshipAssertionRow(Guid AssertionId, SemanticAtomId Subject,
    SemanticAtomId Relationship, SemanticAtomId Target, SemanticAtomId Provenance,
    DatabaseVersion BeginVersion, DatabaseVersion? EndVersion);

internal sealed class SemanticAssertionRowCodec
{
    public const byte FormatVersion = 1;
    public const int EncodedLength = sizeof(byte) * 3 + 16 + SemanticAtomId.ByteLength * 4 + sizeof(long) * 2;
    private const int VersionOffset = 0;
    private const int KindOffset = VersionOffset + sizeof(byte);
    private const int HasEndVersionOffset = KindOffset + sizeof(byte);
    private const int AssertionOffset = HasEndVersionOffset + sizeof(byte);
    private const int FirstAtomOffset = AssertionOffset + 16;
    private const int BeginVersionOffset = FirstAtomOffset + SemanticAtomId.ByteLength * 4;
    private const int EndVersionOffset = BeginVersionOffset + sizeof(long);

    public byte[] Encode(SemanticFieldAssertionRow row) => Encode(row.AssertionId,
        SemanticAssertionRowKind.Field, row.Subject, row.Field, row.Value, row.Provenance,
        row.BeginVersion, row.EndVersion);

    public byte[] Encode(SemanticRelationshipAssertionRow row) => Encode(row.AssertionId,
        SemanticAssertionRowKind.Relationship, row.Subject, row.Relationship, row.Target, row.Provenance,
        row.BeginVersion, row.EndVersion);

    public SemanticFieldAssertionRow DecodeField(ReadOnlySpan<byte> bytes)
    {
        DecodedRow row = DecodeReferences(bytes, SemanticAssertionRowKind.Field);
        return new(row.AssertionId, row.First, row.Second, row.Third, row.Fourth,
            row.BeginVersion, row.EndVersion);
    }

    public SemanticRelationshipAssertionRow DecodeRelationship(ReadOnlySpan<byte> bytes)
    {
        DecodedRow row = DecodeReferences(bytes, SemanticAssertionRowKind.Relationship);
        return new(row.AssertionId, row.First, row.Second, row.Third, row.Fourth,
            row.BeginVersion, row.EndVersion);
    }

    private static byte[] Encode(Guid assertionId, SemanticAssertionRowKind kind,
        SemanticAtomId first, SemanticAtomId second, SemanticAtomId third, SemanticAtomId fourth,
        DatabaseVersion beginVersion, DatabaseVersion? endVersion)
    {
        if (assertionId == Guid.Empty)
        {
            throw new ArgumentException("An assertion ID cannot be empty.", nameof(assertionId));
        }

        if (beginVersion.Value < 0 || endVersion?.Value < 0 || endVersion?.Value <= beginVersion.Value)
        {
            throw new ArgumentException("Assertion version bounds are invalid.", nameof(endVersion));
        }

        byte[] result = new byte[EncodedLength];
        result[VersionOffset] = FormatVersion;
        result[KindOffset] = (byte)kind;
        result[HasEndVersionOffset] = endVersion is null ? (byte)0 : (byte)1;
        assertionId.TryWriteBytes(result.AsSpan(AssertionOffset, 16));
        first.Write(result.AsSpan(FirstAtomOffset, SemanticAtomId.ByteLength));
        second.Write(result.AsSpan(FirstAtomOffset + SemanticAtomId.ByteLength, SemanticAtomId.ByteLength));
        third.Write(result.AsSpan(FirstAtomOffset + SemanticAtomId.ByteLength * 2, SemanticAtomId.ByteLength));
        fourth.Write(result.AsSpan(FirstAtomOffset + SemanticAtomId.ByteLength * 3, SemanticAtomId.ByteLength));
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(BeginVersionOffset), beginVersion.Value);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(EndVersionOffset), endVersion?.Value ?? 0);
        return result;
    }

    internal static DecodedRow DecodeReferences(ReadOnlySpan<byte> bytes, SemanticAssertionRowKind expectedKind)
    {
        if (bytes.Length != EncodedLength || bytes[VersionOffset] != FormatVersion ||
            bytes[KindOffset] != (byte)expectedKind || bytes[HasEndVersionOffset] > 1)
        {
            throw new InvalidDataException("Semantic assertion row header is invalid.");
        }

        var assertionId = new Guid(bytes.Slice(AssertionOffset, 16));
        if (assertionId == Guid.Empty)
        {
            throw new InvalidDataException("Semantic assertion row ID is empty.");
        }

        var begin = new DatabaseVersion(BinaryPrimitives.ReadInt64LittleEndian(bytes[BeginVersionOffset..]));
        long encodedEnd = BinaryPrimitives.ReadInt64LittleEndian(bytes[EndVersionOffset..]);
        DatabaseVersion? end = bytes[HasEndVersionOffset] == 0 ? null : new(encodedEnd);
        if (begin.Value < 0 || end?.Value <= begin.Value || end is null && encodedEnd != 0)
        {
            throw new InvalidDataException("Semantic assertion row version bounds are invalid.");
        }

        return new(assertionId,
            SemanticAtomId.Read(bytes[FirstAtomOffset..]),
            SemanticAtomId.Read(bytes[(FirstAtomOffset + SemanticAtomId.ByteLength)..]),
            SemanticAtomId.Read(bytes[(FirstAtomOffset + SemanticAtomId.ByteLength * 2)..]),
            SemanticAtomId.Read(bytes[(FirstAtomOffset + SemanticAtomId.ByteLength * 3)..]), begin, end);
    }

    internal readonly record struct DecodedRow(Guid AssertionId, SemanticAtomId First, SemanticAtomId Second,
        SemanticAtomId Third, SemanticAtomId Fourth, DatabaseVersion BeginVersion,
        DatabaseVersion? EndVersion);
}
