using System.Buffers.Binary;

namespace EntityForge.Db.Storage.Pages;

// Owns the canonical run-record bytes, independently of mutable page buffers.
// Physical ownership belongs to the manifest containing this payload, never to
// invented data-page extents. The manifest checksum protects the serialized bytes.
internal sealed class AppendMergeInlinePayload
{
    internal const int MaximumBytes = 4096;
    private readonly byte[] _bytes;
    private readonly IReadOnlyList<ReadOnlyMemory<byte>> _records;

    private AppendMergeInlinePayload(byte[] bytes, IReadOnlyList<ReadOnlyMemory<byte>> records)
    {
        _bytes = bytes;
        _records = records;
    }

    internal ReadOnlyMemory<byte> Bytes => _bytes;

    internal IReadOnlyList<ReadOnlyMemory<byte>> Records => _records;

    internal static AppendMergeInlinePayload? TryCreate<TKey, TValue>(AppendMergeRunPlan<TKey, TValue> plan,
        AppendMergeRunCodec<TKey, TValue> codec) where TKey : notnull
    {
        long length = sizeof(int) + plan.Entries.Sum(entry => sizeof(int) + (long)entry.RecordLength);
        if (plan.Entries.Count == 0 || length > MaximumBytes)
        {
            return null;
        }
        // The buffer is created and retained here; callers never receive mutable
        // ownership. Encode each prepared record directly into its final slice.
        byte[] bytes = new byte[(int)length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, plan.Entries.Count);
        int offset = sizeof(int);
        foreach (var entry in plan.Entries)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), entry.RecordLength);
            offset += sizeof(int);
            codec.Encode(entry.Entry, entry.Key, entry.Value, bytes.AsSpan(offset, entry.RecordLength));
            offset += entry.RecordLength;
        }
        return ParseOwned(bytes, plan.Entries.Count);
    }

    internal static AppendMergeInlinePayload? TryCreate(IReadOnlyList<byte[]> records, int availableBytes)
    {
        if (records.Count == 0 || records.Any(record => record.Length == 0))
        {
            throw new ArgumentException("An inline run must contain nonempty records.", nameof(records));
        }

        long length = sizeof(int) + records.Sum(record => sizeof(int) + (long)record.Length);
        if (length > Math.Min(availableBytes, MaximumBytes))
        {
            return null;
        }

        byte[] bytes = new byte[(int)length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, records.Count);
        int offset = sizeof(int);
        foreach (byte[] record in records)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), record.Length);
            offset += sizeof(int);
            record.CopyTo(bytes, offset);
            offset += record.Length;
        }
        return ParseOwned(bytes, records.Count);
    }

    internal static AppendMergeInlinePayload Decode(ReadOnlySpan<byte> bytes, long expectedCount)
    {
        if (bytes.Length < sizeof(int) || bytes.Length > MaximumBytes)
        {
            throw Corrupt();
        }

        return ParseOwned(bytes.ToArray(), expectedCount);
    }

    private static AppendMergeInlinePayload ParseOwned(byte[] bytes, long expectedCount)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (count <= 0 || count != expectedCount || count > (bytes.Length - sizeof(int)) / 5)
        {
            throw Corrupt();
        }

        var records = new ReadOnlyMemory<byte>[count];
        int offset = sizeof(int);
        for (int index = 0; index < count; index++)
        {
            if (bytes.Length - offset < sizeof(int))
            {
                throw Corrupt();
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            offset += sizeof(int);
            if (length <= 0 || length > bytes.Length - offset)
            {
                throw Corrupt();
            }

            records[index] = bytes.AsMemory(offset, length);
            offset += length;
        }
        if (offset != bytes.Length)
        {
            throw Corrupt();
        }

        return new(bytes, Array.AsReadOnly(records));
    }

    private static PhysicalPageCorruptionException Corrupt() => new("Inline run payload framing is invalid.");
}
