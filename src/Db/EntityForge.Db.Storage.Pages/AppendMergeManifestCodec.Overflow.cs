using System.Buffers.Binary;

namespace EntityForge.Db.Storage.Pages;

internal sealed partial class AppendMergeManifestCodec
{
    private const byte OverflowRunRecordKind = 59;
    private const byte OverflowOwnershipKind = 60;
    private const int OverflowOwnershipHeaderLength = 21;

    private sealed record OverflowOwnershipChunk(long Generation, int Index, int Count, PhysicalPageExtent[] Extents);

    private static int OverflowRecordCount(AppendMergeRunDescriptor run) =>
        checked((int)((run.OverflowExtents.Count + (long)MaximumExtentsPerRunRecord - 1) / MaximumExtentsPerRunRecord));

    private static IEnumerable<byte[]> EncodeOverflowOwnership(AppendMergeRunDescriptor run)
    {
        ValidateRunOwnership(run);
        int chunks = OverflowRecordCount(run);
        for (int chunk = 0; chunk < chunks; chunk++)
        {
            int first = chunk * MaximumExtentsPerRunRecord;
            int count = Math.Min(MaximumExtentsPerRunRecord, run.OverflowExtents.Count - first);
            byte[] record = new byte[OverflowOwnershipHeaderLength + count * ExtentLength];
            record[0] = OverflowOwnershipKind;
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(1), run.Generation);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(9), chunk);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(13), chunks);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(17), count);
            WriteExtents(record.AsSpan(OverflowOwnershipHeaderLength), run.OverflowExtents, first, count);
            yield return record;
        }
    }

    private static OverflowOwnershipChunk DecodeOverflowOwnership(ReadOnlySpan<byte> record)
    {
        if (record.Length < OverflowOwnershipHeaderLength || record[0] != OverflowOwnershipKind)
        {
            throw Corrupt("Overflow ownership record is invalid.");
        }
        long generation = BinaryPrimitives.ReadInt64LittleEndian(record[1..]);
        int index = BinaryPrimitives.ReadInt32LittleEndian(record[9..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(record[13..]);
        int extents = BinaryPrimitives.ReadInt32LittleEndian(record[17..]);
        if (generation <= 0 || index < 0 || count <= 0 || index >= count || extents <= 0 ||
            extents > MaximumExtentsPerRunRecord || record.Length != OverflowOwnershipHeaderLength + (long)extents * ExtentLength)
        {
            throw Corrupt("Overflow ownership bounds are invalid.");
        }
        var result = new PhysicalPageExtent[extents];
        try
        {
            for (int item = 0; item < extents; item++)
            {
                int offset = OverflowOwnershipHeaderLength + item * ExtentLength;
                result[item] = new(new(BinaryPrimitives.ReadInt64LittleEndian(record[offset..])),
                    BinaryPrimitives.ReadInt64LittleEndian(record[(offset + sizeof(long))..]));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw Corrupt("Overflow ownership extent is invalid.");
        }
        return new(generation, index, count, result);
    }

    private static PhysicalPageExtent[] CombineOverflowOwnership(long generation, bool required,
        Dictionary<long, List<OverflowOwnershipChunk>> pending)
    {
        bool found = pending.Remove(generation, out var fragments);
        if (found != required)
        {
            throw Corrupt("Run overflow marker and ownership metadata disagree.");
        }
        if (!found)
        {
            return [];
        }
        var chunks = fragments!.OrderBy(chunk => chunk.Index).ToArray();
        if (chunks.Length != chunks[0].Count || chunks.Any(chunk => chunk.Count != chunks.Length) ||
            !chunks.Select(chunk => chunk.Index).SequenceEqual(Enumerable.Range(0, chunks.Length)))
        {
            throw Corrupt("Overflow ownership chunks are missing or duplicated.");
        }
        return chunks.SelectMany(chunk => chunk.Extents).ToArray();
    }

    private static void ValidateRunOwnership(AppendMergeRunDescriptor run)
    {
        long end = -1;
        foreach (var extent in run.OwnedExtents.OrderBy(extent => extent.FirstPage.Value))
        {
            if (extent.FirstPage.Value < end)
            {
                throw Corrupt("Run data and overflow ownership overlap.");
            }
            end = extent.EndExclusive;
        }
    }

    internal static void ValidateManifestOwnership(IReadOnlyList<AppendMergeRunDescriptor> runs,
        IReadOnlyList<AppendMergeRetiredPages> retired, IReadOnlyList<PhysicalPageId> manifestPages)
    {
        // Ownership is exclusive across live runs, deferred retirement and metadata.
        // Compare intervals rather than expanding potentially corrupt page counts.
        var extents = runs.SelectMany(run => run.OwnedExtents)
            .Concat(retired.SelectMany(item => item.Extents))
            .Concat(manifestPages.Select(page => new PhysicalPageExtent(page, 1)))
            .OrderBy(extent => extent.FirstPage.Value);
        long end = -1;
        foreach (var extent in extents)
        {
            if (extent.FirstPage.Value < end)
            {
                throw Corrupt("Manifest live, retired or metadata page ownership overlaps.");
            }
            end = extent.EndExclusive;
        }
    }
}
