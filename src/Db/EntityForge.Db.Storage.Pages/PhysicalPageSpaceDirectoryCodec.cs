using System.Buffers.Binary;
using System.Text;

namespace EntityForge.Db.Storage.Pages;

internal sealed class PhysicalPageSpaceDirectoryCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const byte RootRecordKind = 41;
    private const byte EntryRecordKind = 42;
    private const int FormatVersion = 3;
    private const int MaximumExtentsPerEntryRecord = 512;
    private const int RootHeaderLength = sizeof(byte) + sizeof(int) + sizeof(long) + sizeof(int) * 2;
    private const int EntryHeaderLength = sizeof(byte) + 16 + sizeof(int) * 5 + sizeof(long) * 2;
    private const int ExtentLength = sizeof(long) * 2;

    public PhysicalPageSpaceDirectoryImage Encode(PhysicalPageSpaceDirectory directory,
        PhysicalPageSpaceTransaction transaction, IReadOnlyList<PhysicalPageId> activePages,
        IReadOnlyList<PhysicalPageId> standbyPages, PhysicalPageSpaceDirectory? previousDirectory = null,
        bool inlineEntries = false)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(activePages);
        ArgumentNullException.ThrowIfNull(standbyPages);
        List<IReadOnlyList<byte[]>> packed = Pack(directory);
        if (inlineEntries && packed.Count <= 1)
        {
            var records = packed.SelectMany(group => group).ToArray();
            var reserved = activePages.Concat(standbyPages).ToArray();
            long bytes = SlottedDataPage.HeaderSize + SlottedDataPage.SlotSize + RootHeaderLength +
                (long)reserved.Length * sizeof(long) + records.Sum(record => (long)record.Length + SlottedDataPage.SlotSize);
            if (bytes <= PhysicalPageFileOptions.DefaultPageSize)
            {
                var inlineRoot = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize,
                    new(PhysicalPageSpaceDirectory.DirectoryRootPageId), directory.Generation);
                byte[] header = new byte[RootHeaderLength + reserved.Length * sizeof(long)];
                header[0] = RootRecordKind;
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), 4);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(5), directory.Generation);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(17), reserved.Length);
                for (int index = 0; index < reserved.Length; index++)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(RootHeaderLength + index * sizeof(long)), reserved[index].Value);
                }

                if (!inlineRoot.TryInsert(header, directory.Generation, out _))
                {
                    throw new PhysicalPageCapacityException("Inline directory header exceeds capacity.");
                }

                foreach (var record in records)
                {
                    if (!inlineRoot.TryInsert(record, directory.Generation, out _))
                    {
                        throw new PhysicalPageCapacityException("Inline directory entries exceed capacity.");
                    }
                }
                // Former branch pages remain reserved standby until normal reclamation.
                // They cannot be reused as access-path data while an old root may refer to them.
                return new(inlineRoot.EncodeOwned(), [], [], reserved);
            }
        }
        int required = packed.Count;
        var previous = previousDirectory is null ? null : Pack(previousDirectory);
        var reuse = new bool[required];
        if (previous?.Count == activePages.Count)
        {
            for (int index = 0; index < Math.Min(required, previous.Count); index++)
            {
                reuse[index] = packed[index].Count == previous[index].Count &&
                    packed[index].Select((record, slot) => record.AsSpan().SequenceEqual(previous[index][slot])).All(equal => equal);
            }
        }

        int changed = required - reuse.Count(value => value);
        var candidates = standbyPages.ToList();
        int totalReserved = activePages.Count + standbyPages.Count;
        int allocationCount = Math.Max(changed - candidates.Count,
            required * 2 - totalReserved);
        for (int index = 0; index < allocationCount; index++)
        {
            candidates.Add(transaction.AllocateDirectoryPage());
        }

        var targetPages = new PhysicalPageId[required];
        int candidate = 0;
        for (int index = 0; index < required; index++)
        {
            targetPages[index] = reuse[index] ? activePages[index] : candidates[candidate++];
        }

        var retained = targetPages.ToHashSet();
        PhysicalPageId[] nextStandby = activePages.Where(page => !retained.Contains(page))
            .Concat(candidates.Skip(changed)).ToArray();
        var branches = new List<KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>>>();
        for (int index = 0; index < packed.Count; index++)
        {
            // Unchanged immutable records retain their published branch page. Only
            // the new root changes; its generation need not equal branch generations.
            if (reuse[index])
            {
                continue;
            }

            var branch = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize,
                targetPages[index], directory.Generation);
            foreach (byte[] record in packed[index])
            {
                if (!branch.TryInsert(record, directory.Generation, out _))
                {
                    throw new PhysicalPageCapacityException("A packed directory branch exceeds page capacity.");
                }
            }

            branches.Add(new(branch.PageId, branch.EncodeOwned()));
        }
        var root = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize,
            new(PhysicalPageSpaceDirectory.DirectoryRootPageId), directory.Generation);
        byte[] rootRecord = new byte[RootHeaderLength +
            (targetPages.Length + nextStandby.Length) * sizeof(long)];
        rootRecord[0] = RootRecordKind;
        BinaryPrimitives.WriteInt32LittleEndian(rootRecord.AsSpan(sizeof(byte)), FormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(rootRecord.AsSpan(sizeof(byte) + sizeof(int)), directory.Generation);
        BinaryPrimitives.WriteInt32LittleEndian(rootRecord.AsSpan(sizeof(byte) + sizeof(int) + sizeof(long)),
            targetPages.Length);
        BinaryPrimitives.WriteInt32LittleEndian(
            rootRecord.AsSpan(sizeof(byte) + sizeof(int) + sizeof(long) + sizeof(int)),
            nextStandby.Length);
        int offset = RootHeaderLength;
        foreach (PhysicalPageId page in targetPages)
        {
            BinaryPrimitives.WriteInt64LittleEndian(rootRecord.AsSpan(offset), page.Value);
            offset += sizeof(long);
        }
        foreach (PhysicalPageId page in nextStandby)
        {
            BinaryPrimitives.WriteInt64LittleEndian(rootRecord.AsSpan(offset), page.Value);
            offset += sizeof(long);
        }
        if (!root.TryInsert(rootRecord, directory.Generation, out _))
        {
            throw new PhysicalPageCapacityException("The page-space directory root exceeds page capacity.");
        }

        return new(root.EncodeOwned(), branches, targetPages, nextStandby);
    }

    public PhysicalPageSpaceDirectoryRoot DecodeRoot(ReadOnlySpan<byte> image)
    {
        SlottedDataPage page = SlottedDataPage.Decode(image,
            new(PhysicalPageSpaceDirectory.DirectoryRootPageId));
        if (page.OccupiedRecords.Count == 0)
        {
            throw Corrupt("Directory root record count is invalid.");
        }

        ReadOnlySpan<byte> record = page.OccupiedRecords[0].Span;
        if (record.Length < RootHeaderLength || record[0] != RootRecordKind)
        {
            throw Corrupt("Directory root record is invalid.");
        }

        int format = BinaryPrimitives.ReadInt32LittleEndian(record[sizeof(byte)..]);
        long generation = BinaryPrimitives.ReadInt64LittleEndian(record[(sizeof(byte) + sizeof(int))..]);
        int branchCount = BinaryPrimitives.ReadInt32LittleEndian(
            record[(sizeof(byte) + sizeof(int) + sizeof(long))..]);
        int standbyCount = BinaryPrimitives.ReadInt32LittleEndian(
            record[(sizeof(byte) + sizeof(int) + sizeof(long) + sizeof(int))..]);
        if (format is not (FormatVersion or 4) || generation < 0 || branchCount < 0 || standbyCount < 0 ||
            (format == FormatVersion && page.OccupiedRecords.Count != 1) || (format == 4 && branchCount != 0) ||
            record.Length != RootHeaderLength +
            (branchCount + standbyCount) * (long)sizeof(long))
        {
            throw Corrupt("Directory root bounds are invalid.");
        }

        var branches = new PhysicalPageId[branchCount];
        int offset = RootHeaderLength;
        for (int index = 0; index < branchCount; index++)
        {
            branches[index] = new(BinaryPrimitives.ReadInt64LittleEndian(record[offset..]));
            if (branches[index].Value < PhysicalPageSpaceDirectory.FirstAccessMethodPageId)
            {
                throw Corrupt("Directory branch page is invalid.");
            }

            offset += sizeof(long);
        }
        if (branches.Distinct().Count() != branches.Length)
        {
            throw Corrupt("A directory branch page is duplicated.");
        }

        var standby = new PhysicalPageId[standbyCount];
        for (int index = 0; index < standby.Length; index++)
        {
            standby[index] = new(BinaryPrimitives.ReadInt64LittleEndian(record[offset..]));
            if (standby[index].Value < PhysicalPageSpaceDirectory.FirstAccessMethodPageId)
            {
                throw Corrupt("Standby directory branch page is invalid.");
            }

            offset += sizeof(long);
        }
        if (standby.Distinct().Count() != standby.Length || branches.Intersect(standby).Any())
        {
            throw Corrupt("Directory branch ownership is duplicated.");
        }

        return new(generation, branches, standby)
        {
            InlineEntries = format == 4 ? AssembleEntries(page.OccupiedRecords.Skip(1).Select(item => DecodeEntry(item.Span)).ToArray()) : []
        };
    }

    public IReadOnlyList<PhysicalAccessPathDirectoryEntry> DecodeBranches(
        IEnumerable<(PhysicalPageId PageId, ReadOnlyMemory<byte> Image)> branches)
    {
        DirectoryEntryFragment[] fragments = branches.SelectMany(branch =>
                SlottedDataPage.ReadOccupiedRecords(branch.Image, branch.PageId)
                    .Select(record => DecodeEntry(record.Span)))
            .ToArray();
        return AssembleEntries(fragments);
    }

    private static IReadOnlyList<PhysicalAccessPathDirectoryEntry> AssembleEntries(DirectoryEntryFragment[] fragments)
    {
        var entries = new List<PhysicalAccessPathDirectoryEntry>();
        foreach (IGrouping<PhysicalAccessPathId, DirectoryEntryFragment> group in
                 fragments.GroupBy(fragment => fragment.AccessPathId))
        {
            DirectoryEntryFragment[] chunks = group.OrderBy(fragment => fragment.ChunkIndex).ToArray();
            DirectoryEntryFragment first = chunks[0];
            if (chunks.Length != first.ChunkCount || chunks.Select(chunk => chunk.ChunkIndex)
                    .SequenceEqual(Enumerable.Range(0, first.ChunkCount)) is false ||
                chunks.Any(chunk => chunk.ChunkCount != first.ChunkCount || chunk.Method != first.Method ||
                                    chunk.RootPage != first.RootPage || chunk.Generation != first.Generation))
            {
                throw Corrupt("Directory entry chunks are incomplete or inconsistent.");
            }

            entries.Add(new(first.AccessPathId, first.Method, first.RootPage, first.Generation,
                chunks.SelectMany(chunk => chunk.Extents).ToArray()));
        }
        return entries;
    }

    private static List<IReadOnlyList<byte[]>> Pack(PhysicalPageSpaceDirectory directory)
    {
        var result = new List<IReadOnlyList<byte[]>>();
        var records = new List<byte[]>();
        // These pages contain only new records: no reusable slots or fragmentation.
        // Account for the same header/slot/payload bytes without allocating a scratch image.
        int capacity = PhysicalPageFileOptions.DefaultPageSize - SlottedDataPage.HeaderSize;
        int remaining = capacity;
        foreach (PhysicalAccessPathDirectoryEntry entry in directory.Entries)
        {
            int chunkCount = (entry.Extents.Count + MaximumExtentsPerEntryRecord - 1) /
                             MaximumExtentsPerEntryRecord;
            for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                byte[] record = EncodeEntry(entry, chunkIndex, chunkCount);
                int required = checked(record.Length + SlottedDataPage.SlotSize);
                if (record.Length > ushort.MaxValue || required > capacity)
                {
                    throw new PhysicalPageCapacityException(
                        "A page-space directory entry chunk exceeds page capacity.");
                }

                if (required <= remaining)
                {
                    records.Add(record);
                    remaining -= required;
                    continue;
                }
                if (records.Count == 0)
                {
                    throw new PhysicalPageCapacityException(
                        "A page-space directory entry chunk exceeds page capacity.");
                }

                result.Add(records.ToArray());
                records = [record];
                remaining = capacity - required;
            }
        }
        if (records.Count != 0)
        {
            result.Add(records.ToArray());
        }

        return result;
    }

    private static byte[] EncodeEntry(PhysicalAccessPathDirectoryEntry entry, int chunkIndex,
        int chunkCount)
    {
        int firstExtent = chunkIndex * MaximumExtentsPerEntryRecord;
        int extentCount = Math.Min(MaximumExtentsPerEntryRecord, entry.Extents.Count - firstExtent);
        int methodLength = StrictUtf8.GetByteCount(entry.Method.Name);
        int length = checked(EntryHeaderLength + methodLength + extentCount * ExtentLength);
        byte[] result = new byte[length];
        result[0] = EntryRecordKind;
        entry.AccessPathId.Value.TryWriteBytes(result.AsSpan(sizeof(byte), 16));
        int offset = sizeof(byte) + 16;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), methodLength);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), entry.Method.FormatVersion);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(offset), entry.RootPage.Value);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(offset), entry.Generation);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), chunkIndex);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), chunkCount);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), extentCount);
        offset += sizeof(int);
        StrictUtf8.GetBytes(entry.Method.Name, result.AsSpan(offset, methodLength));
        offset += methodLength;
        for (int index = 0; index < extentCount; index++)
        {
            PhysicalPageExtent extent = entry.Extents[firstExtent + index];
            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(offset), extent.FirstPage.Value);
            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(offset + sizeof(long)), extent.PageCount);
            offset += ExtentLength;
        }
        return result;
    }

    private static DirectoryEntryFragment DecodeEntry(ReadOnlySpan<byte> record)
    {
        if (record.Length < EntryHeaderLength || record[0] != EntryRecordKind)
        {
            throw Corrupt("Directory entry record is invalid.");
        }

        var id = new Guid(record.Slice(sizeof(byte), 16));
        int offset = sizeof(byte) + 16;
        int methodLength = BinaryPrimitives.ReadInt32LittleEndian(record[offset..]);
        offset += sizeof(int);
        int methodVersion = BinaryPrimitives.ReadInt32LittleEndian(record[offset..]);
        offset += sizeof(int);
        long rootPage = BinaryPrimitives.ReadInt64LittleEndian(record[offset..]);
        offset += sizeof(long);
        long generation = BinaryPrimitives.ReadInt64LittleEndian(record[offset..]);
        offset += sizeof(long);
        int chunkIndex = BinaryPrimitives.ReadInt32LittleEndian(record[offset..]);
        offset += sizeof(int);
        int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(record[offset..]);
        offset += sizeof(int);
        int extentCount = BinaryPrimitives.ReadInt32LittleEndian(record[offset..]);
        offset += sizeof(int);
        long expectedLength = EntryHeaderLength + (long)methodLength + extentCount * (long)ExtentLength;
        if (id == Guid.Empty || methodLength <= 0 || methodVersion <= 0 || rootPage < 1 ||
            generation < 0 || chunkIndex < 0 || chunkCount <= 0 || chunkIndex >= chunkCount ||
            extentCount <= 0 || extentCount > MaximumExtentsPerEntryRecord || record.Length != expectedLength)
        {
            throw Corrupt("Directory entry bounds are invalid.");
        }

        string methodName;
        try
        {
            methodName = StrictUtf8.GetString(record.Slice(offset, methodLength));
        }
        catch (DecoderFallbackException)
        {
            throw Corrupt("Directory access-method name is not valid UTF-8.");
        }
        offset += methodLength;
        var extents = new PhysicalPageExtent[extentCount];
        try
        {
            for (int index = 0; index < extentCount; index++)
            {
                extents[index] = new(new(BinaryPrimitives.ReadInt64LittleEndian(record[offset..])),
                    BinaryPrimitives.ReadInt64LittleEndian(record[(offset + sizeof(long))..]));
                offset += ExtentLength;
            }
            return new(new(id), new(methodName, methodVersion), new(rootPage), generation,
                chunkIndex, chunkCount, extents);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new PhysicalPageCorruptionException("Directory entry ownership is invalid.");
        }
    }

    private static PhysicalPageCorruptionException Corrupt(string message) => new(message);

    private sealed record DirectoryEntryFragment(PhysicalAccessPathId AccessPathId,
        PhysicalAccessMethodKind Method, PhysicalPageId RootPage, long Generation,
        int ChunkIndex, int ChunkCount, IReadOnlyList<PhysicalPageExtent> Extents);
}

internal sealed record PhysicalPageSpaceDirectoryRoot(long Generation,
    IReadOnlyList<PhysicalPageId> BranchPages,
    IReadOnlyList<PhysicalPageId> StandbyBranchPages)
{
    public IReadOnlyList<PhysicalAccessPathDirectoryEntry> InlineEntries { get; init; } = [];
}

internal sealed record PhysicalPageSpaceDirectoryImage(ReadOnlyMemory<byte> Root,
    IReadOnlyList<KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>>> Branches,
    IReadOnlyList<PhysicalPageId> BranchPageIds,
    IReadOnlyList<PhysicalPageId> StandbyBranchPageIds);
