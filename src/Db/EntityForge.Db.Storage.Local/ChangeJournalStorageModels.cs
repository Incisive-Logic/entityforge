using EntityForge.Db;
using System.Text;

namespace EntityForge.Db.Storage.Local;

internal sealed record ChangeJournalRoutingEntry(string Kind, string Value);

internal sealed record ChangeJournalRoutingIndex(int CodecVersion, DatabaseProfileId Profile,
    IReadOnlyList<ChangeJournalRoutingEntry> RoutingKeys, IReadOnlyList<string> OverflowedKinds);

[method: System.Text.Json.Serialization.JsonConstructor]
internal readonly record struct ChangeJournalSegmentDescriptor(string Id, string SegmentFile, string IndexFile,
    long Version, Guid TransactionId, string Cursor, DateTimeOffset CreatedUtc, long Bytes,
    string SegmentChecksum, string IndexChecksum, long? RecordOffset = null, int? RecordLength = null,
    long? IndexOffset = null, int? IndexLength = null)
{
    // Native identifiers duplicate scalar fields already in this descriptor.
    // Preserve arbitrary imported identifiers verbatim, but avoid retaining a
    // separate 53-character string for every ordinary journal record.
    private readonly string? _storedId = RetainedId(Id, Version, TransactionId);

    [System.Text.Json.Serialization.JsonPropertyOrder(-1)]
    public string Id => _storedId ?? $"{Version:D20}-{TransactionId:N}";

    private static string? RetainedId(string id, long version, Guid transactionId)
    {
        ArgumentNullException.ThrowIfNull(id);
        Span<char> expected = stackalloc char[64];
        if (version.TryFormat(expected, out int length, "D20") && length < expected.Length)
        {
            expected[length++] = '-';
            if (transactionId.TryFormat(expected[length..], out int written, "N") &&
                id.AsSpan().SequenceEqual(expected[..(length + written)]))
            {
                return null;
            }
        }
        return id;
    }
}

internal static class ChangeJournalStorageFootprint
{
    // JSON property names, punctuation, numeric fields and object/collection framing. String values
    // are counted separately so long routing identifiers and future filename changes remain bounded.
    private const int DescriptorEncodingOverhead = 320;
    private const int ManifestEncodingOverhead = 512;
    // One current or stale publication file can coexist with the catalog. Include worst-case
    // JSON escaping of the bounded cursor, its scalar framing and checksum in retention budgeting.
    private const int MaximumJsonEscapeBytes = 6;
    private const int PublicationEncodingAllowance = ManifestEncodingOverhead +
        MaximumJsonEscapeBytes * ChangeJournalBinaryCodec.MaximumStringBytes;

    public static long Measure(ChangeJournalManifest manifest) => checked(
        ManifestEncodingOverhead + PublicationEncodingAllowance *
        (SegmentedDatabaseCommittedChangeStore.MaximumCatalogDeltas + 2L) + manifest.Segments.Sum(Measure));

    public static long Measure(ChangeJournalSegmentDescriptor descriptor) => checked(
        descriptor.Bytes + DescriptorEncodingOverhead +
        Utf8Bytes(descriptor.Id) + Utf8Bytes(descriptor.SegmentFile) + Utf8Bytes(descriptor.IndexFile) +
        Utf8Bytes(descriptor.Cursor) + Utf8Bytes(descriptor.SegmentChecksum) +
        Utf8Bytes(descriptor.IndexChecksum));

    private static int Utf8Bytes(string value) => Encoding.UTF8.GetByteCount(value);
}

internal sealed record ChangeJournalManifest(int CodecVersion, long Generation, Guid? DatabaseIncarnation,
    string? RetentionFloor, long? RetentionFloorVersion, string? PublishedCursor,
    IReadOnlyList<ChangeJournalSegmentDescriptor> Segments)
{
    public const int CurrentCodecVersion = 4;

    public static ChangeJournalManifest Empty
    {
        get;
    }
    =
        new(CurrentCodecVersion, 0, null, null, null, null, Array.Empty<ChangeJournalSegmentDescriptor>());
}

internal sealed record ChangeJournalRetentionResult(
    ChangeJournalManifest Manifest,
    IReadOnlyList<ChangeJournalSegmentDescriptor> Removed);
