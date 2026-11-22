using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EntityForge.Db.Storage.Pages;

// Bounded storage primitive; callers must persist every returned page in their
// ownership metadata and supply that ownership check when following references.
internal static class PhysicalOverflowRecord
{
    internal const byte ReferenceKind = 231;
    internal const byte ChunkKind = 232;
    internal const int ReferenceLength = 54;
    internal const int ChunkHeaderLength = 30;
    internal const int ChunkPayloadBytes = PhysicalPageFileOptions.DefaultPageSize -
        SlottedDataPage.HeaderSize - SlottedDataPage.SlotSize - ChunkHeaderLength;

    internal sealed record Image(byte[] Reference, IReadOnlyDictionary<PhysicalPageId, ReadOnlyMemory<byte>> Pages);

    internal static Image Create(ReadOnlySpan<byte> value, int maximumBytes, long generation, Func<PhysicalPageId> allocate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allocate);
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes < 1 || value.Length < 1 || value.Length > maximumBytes || generation < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Overflow record exceeds its configured bound or has an invalid generation.");
        }
        int count = checked((int)((value.Length + (long)ChunkPayloadBytes - 1) / ChunkPayloadBytes));
        var ids = new PhysicalPageId[count];
        var unique = new HashSet<PhysicalPageId>();
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ids[index] = allocate();
            if (ids[index].Value <= 0 || !unique.Add(ids[index]))
            {
                throw new InvalidOperationException("Overflow allocation returned an invalid or duplicate page.");
            }
        }
        byte[] reference = new byte[ReferenceLength];
        reference[0] = ReferenceKind;
        reference[1] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(reference.AsSpan(2), generation);
        BinaryPrimitives.WriteInt32LittleEndian(reference.AsSpan(10), value.Length);
        BinaryPrimitives.WriteInt64LittleEndian(reference.AsSpan(14), ids[0].Value);
        SHA256.HashData(value, reference.AsSpan(22));
        var pages = new Dictionary<PhysicalPageId, ReadOnlyMemory<byte>>(count);
        int offset = 0;
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(ChunkPayloadBytes, value.Length - offset);
            var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, ids[index], generation);
            if (!page.TryReserve(ChunkHeaderLength + length, generation, out var destination, out _))
            {
                throw new InvalidOperationException("Overflow chunk does not fit its page.");
            }
            var record = destination.Span;
            record[0] = ChunkKind;
            record[1] = 1;
            BinaryPrimitives.WriteInt64LittleEndian(record[2..], generation);
            BinaryPrimitives.WriteInt64LittleEndian(record[10..], ids[0].Value);
            BinaryPrimitives.WriteInt32LittleEndian(record[18..], offset);
            BinaryPrimitives.WriteInt64LittleEndian(record[22..], index + 1 < count ? ids[index + 1].Value : 0);
            value.Slice(offset, length).CopyTo(record[ChunkHeaderLength..]);
            pages.Add(ids[index], page.EncodeOwned());
            offset += length;
        }
        return new(reference, pages);
    }

    internal static async ValueTask<byte[]> ReadAsync(ReadOnlyMemory<byte> reference, int maximumBytes, long generation,
        Func<PhysicalPageId, bool> owns, Func<PhysicalPageId, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owns);
        ArgumentNullException.ThrowIfNull(read);
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes < 1 || generation < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        // Snapshot the reference before any asynchronous I/O or caller mutation.
        if (reference.Length != ReferenceLength)
        {
            throw Corrupt("Invalid overflow reference size.");
        }
        byte[] header = reference.ToArray();
        if (header[0] != ReferenceKind || header[1] != 1 ||
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(2)) != generation)
        {
            throw Corrupt("Invalid overflow reference.");
        }
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(10));
        long head = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(14));
        if (length < 1 || length > maximumBytes || head <= 0)
        {
            throw Corrupt("Overflow reference exceeds its bounds.");
        }
        byte[] result = new byte[length];
        var visited = new HashSet<PhysicalPageId>();
        long next = head;
        int offset = 0;
        while (offset < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (next <= 0)
            {
                throw Corrupt("Overflow chain ended early.");
            }
            var id = new PhysicalPageId(next);
            if (!owns(id) || !visited.Add(id))
            {
                throw Corrupt("Overflow chain contains a foreign or repeated page.");
            }
            var image = await read(id, cancellationToken).ConfigureAwait(false);
            var page = SlottedDataPage.Decode(image.Span, id);
            var records = page.OccupiedRecords;
            int size = Math.Min(ChunkPayloadBytes, length - offset);
            if (page.Generation != generation || records.Count != 1 || records[0].Length != ChunkHeaderLength + size)
            {
                throw Corrupt("Overflow page has invalid generation or chunk bounds.");
            }
            var chunk = records[0].Span;
            if (chunk[0] != ChunkKind || chunk[1] != 1 || BinaryPrimitives.ReadInt64LittleEndian(chunk[2..]) != generation ||
                BinaryPrimitives.ReadInt64LittleEndian(chunk[10..]) != head || BinaryPrimitives.ReadInt32LittleEndian(chunk[18..]) != offset)
            {
                throw Corrupt("Overflow chunk identity or sequence is invalid.");
            }
            next = BinaryPrimitives.ReadInt64LittleEndian(chunk[22..]);
            chunk[ChunkHeaderLength..].CopyTo(result.AsSpan(offset));
            offset += size;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (next != 0 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(result), header.AsSpan(22)))
        {
            throw Corrupt("Overflow chain length or content digest is invalid.");
        }
        return result;
    }

    private static PhysicalPageCorruptionException Corrupt(string message) => new(message);
}
