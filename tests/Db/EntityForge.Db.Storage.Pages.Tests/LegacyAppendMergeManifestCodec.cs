// Frozen pre-optimization byte/allocation oracle (a5833fc3).
using System.Buffers.Binary;

namespace EntityForge.Db.Storage.Pages;

internal sealed class LegacyAppendMergeManifestCodec
{
    private const int BitsPerByte = 8;
    private const int MaximumExtentsPerRunRecord = 256;
    private const int MaximumFilterBytesPerRunRecord = 8 * 1024;
    private const byte RootRecordKind = 52;
    private const byte RunRecordKind = 53;
    private const byte RetiredRecordKind = 54;
    private const int RootHeaderLength = sizeof(byte) + 16 + sizeof(long) + sizeof(int);
    private const int RunHeaderLength = sizeof(byte) + sizeof(long) * 2 + sizeof(int) * 8;
    private const int ExtentLength = sizeof(long) * 2;
    private const int RetiredHeaderLength = sizeof(byte) + sizeof(long) + sizeof(int);
    private const int MaximumRetiredExtentsPerRecord = (PhysicalPageFileOptions.DefaultPageSize -
        SlottedDataPage.HeaderSize - SlottedDataPage.SlotSize - RetiredHeaderLength) / ExtentLength;

    internal static int RetiredRecordCount(IEnumerable<AppendMergeRetiredPages> retired) =>
        checked((int)retired.Sum(item => (item.Extents.Count + (long)MaximumRetiredExtentsPerRecord - 1) /
            MaximumRetiredExtentsPerRecord));

    // One record per page is an upper bound, including fragmented run descriptors.
    // Fall back to dense encoding before dedicated branches reduce the existing root capacity.
    public static bool CanSeparateRuns(IReadOnlyList<AppendMergeRunDescriptor> runs, int retiredRecords)
    {
        long branches = retiredRecords;
        foreach (AppendMergeRunDescriptor run in runs)
        {
            branches += Math.Max(
                (run.Extents.Count + (long)MaximumExtentsPerRunRecord - 1) / MaximumExtentsPerRunRecord,
                (run.Filter.Bits.Length + (long)MaximumFilterBytesPerRunRecord - 1) / MaximumFilterBytesPerRunRecord);
        }

        int maximumBranches = (PhysicalPageFileOptions.DefaultPageSize - SlottedDataPage.HeaderSize -
            SlottedDataPage.SlotSize - RootHeaderLength) / sizeof(long);
        return branches <= maximumBranches;
    }

    public AppendMergeManifestImage Encode(Guid accessPathId, long generation,
        IReadOnlyList<AppendMergeRunDescriptor> runs,
        IReadOnlyList<AppendMergeRetiredPages> retired,
        IPhysicalPageAllocator allocation,
        IReadOnlyDictionary<long, IReadOnlyList<PhysicalPageId>>? reusableRuns = null)
    {
        var branches = new List<KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>>>();
        var runBranches = new Dictionary<long, IReadOnlyList<PhysicalPageId>>();
        var allBranches = new List<PhysicalPageId>();
        if (reusableRuns is null)
        {
            // Full rewrites (maintenance and private replacement workspaces) retain dense packing.
            allBranches.AddRange(WriteRecords(runs.SelectMany(EncodeRun).Concat(retired.SelectMany(EncodeRetired))));
        }
        else
        {
            // Isolate each run's descriptors so later publications can share its immutable pages.
            foreach (AppendMergeRunDescriptor run in runs)
            {
                if (reusableRuns.TryGetValue(run.Generation, out var reused))
                {
                    runBranches.Add(run.Generation, reused);
                    allBranches.AddRange(reused);
                    continue;
                }
                var pages = WriteRecords(EncodeRun(run));
                runBranches.Add(run.Generation, pages);
                allBranches.AddRange(pages);
            }
            allBranches.AddRange(WriteRecords(retired.SelectMany(EncodeRetired)));
        }

        IReadOnlyList<PhysicalPageId> WriteRecords(IEnumerable<byte[]> records)
        {
            var pages = new List<PhysicalPageId>();
            SlottedDataPage? branch = null;
            foreach (byte[] record in records)
            {
                branch ??= NewPage(allocation, generation);
                if (branch.TryInsert(record, generation, out _))
                {
                    continue;
                }

                if (branch.SlotCount == 0)
                {
                    throw new PhysicalPageCapacityException("An append/merge run descriptor exceeds page capacity.");
                }

                Finish();
                branch = NewPage(allocation, generation);
                if (!branch.TryInsert(record, generation, out _))
                {
                    throw new PhysicalPageCapacityException("An append/merge run descriptor exceeds page capacity.");
                }
            }
            if (branch is not null)
            {
                Finish();
            }

            return pages.ToArray();

            void Finish()
            {
                pages.Add(branch!.PageId);
                branches.Add(new(branch.PageId, branch.EncodeOwned()));
            }
        }
        PhysicalPageId rootPageId = allocation.AllocatePage();
        var root = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, rootPageId, generation);
        byte[] rootRecord = new byte[RootHeaderLength + allBranches.Count * sizeof(long)];
        rootRecord[0] = RootRecordKind;
        accessPathId.TryWriteBytes(rootRecord.AsSpan(sizeof(byte), 16));
        BinaryPrimitives.WriteInt64LittleEndian(rootRecord.AsSpan(sizeof(byte) + 16), generation);
        BinaryPrimitives.WriteInt32LittleEndian(rootRecord.AsSpan(sizeof(byte) + 16 + sizeof(long)), allBranches.Count);
        int offset = RootHeaderLength;
        foreach (PhysicalPageId page in allBranches)
        {
            BinaryPrimitives.WriteInt64LittleEndian(rootRecord.AsSpan(offset), page.Value);
            offset += sizeof(long);
        }
        if (!root.TryInsert(rootRecord, generation, out _))
        {
            throw new PhysicalPageCapacityException("The append/merge manifest root exceeds page capacity.");
        }

        return new(rootPageId, root.EncodeOwned(), branches)
        {
            RunBranches = runBranches,
            AllBranches = allBranches.ToArray()
        };
    }

    public AppendMergeManifestRoot DecodeRoot(ReadOnlySpan<byte> image, PhysicalPageId pageId)
    {
        SlottedDataPage page = SlottedDataPage.Decode(image, pageId);
        if (page.OccupiedRecords.Count != 1)
        {
            throw Corrupt("Append/merge manifest root count is invalid.");
        }

        ReadOnlySpan<byte> record = page.OccupiedRecords[0].Span;
        if (record.Length < RootHeaderLength || record[0] != RootRecordKind)
        {
            throw Corrupt("Append/merge manifest root is invalid.");
        }

        Guid accessPathId = new(record.Slice(sizeof(byte), 16));
        long generation = BinaryPrimitives.ReadInt64LittleEndian(record[(sizeof(byte) + 16)..]);
        int branchCount = BinaryPrimitives.ReadInt32LittleEndian(record[(sizeof(byte) + 16 + sizeof(long))..]);
        if (accessPathId == Guid.Empty || generation <= 0 || branchCount < 0 ||
            record.Length != RootHeaderLength + branchCount * (long)sizeof(long))
        {
            throw Corrupt("Append/merge manifest root bounds are invalid.");
        }

        var branches = new PhysicalPageId[branchCount];
        int offset = RootHeaderLength;
        for (int index = 0; index < branchCount; index++)
        {
            branches[index] = new(BinaryPrimitives.ReadInt64LittleEndian(record[offset..]));
            offset += sizeof(long);
        }
        if (branches.Any(branch => branch.Value <= 0) || branches.Distinct().Count() != branches.Length)
        {
            throw Corrupt("Append/merge manifest branches are invalid.");
        }

        return new(accessPathId, generation, branches);
    }

    public AppendMergeManifestBranch DecodeBranches(
        IEnumerable<(PhysicalPageId PageId, ReadOnlyMemory<byte> Image)> branches)
    {
        var runFragments = new List<RunFragment>();
        var retired = new List<AppendMergeRetiredPages>();
        var runPages = new Dictionary<long, List<PhysicalPageId>>();
        var mixedRuns = new HashSet<long>();
        foreach ((PhysicalPageId pageId, ReadOnlyMemory<byte> image) in branches)
        {
            var generations = new HashSet<long>();
            bool hasRetired = false;
            foreach (ReadOnlyMemory<byte> item in SlottedDataPage.Decode(image.Span, pageId).OccupiedRecords)
            {
                if (item.Span.Length != 0 && item.Span[0] == RetiredRecordKind)
                {
                    retired.Add(DecodeRetired(item.Span));
                    hasRetired = true;
                }
                else
                {
                    RunFragment fragment = DecodeRun(item.Span);
                    runFragments.Add(fragment);
                    generations.Add(fragment.Generation);
                }
            }
            foreach (long generation in generations)
            {
                if (!runPages.TryGetValue(generation, out var pages))
                {
                    runPages.Add(generation, pages = []);
                }

                pages.Add(pageId);
                if (hasRetired || generations.Count != 1)
                {
                    mixedRuns.Add(generation);
                }
            }
        }
        var runs = new List<AppendMergeRunDescriptor>();
        foreach (IGrouping<long, RunFragment> group in runFragments.GroupBy(fragment => fragment.Generation))
        {
            RunFragment[] chunks = group.OrderBy(fragment => fragment.ChunkIndex).ToArray();
            RunFragment first = chunks[0];
            if (chunks.Length != first.ChunkCount || chunks.Select(chunk => chunk.ChunkIndex)
                    .SequenceEqual(Enumerable.Range(0, first.ChunkCount)) is false ||
                chunks.Any(chunk => chunk.ChunkCount != first.ChunkCount ||
                                    chunk.EntryCount != first.EntryCount ||
                                    !chunk.MinimumKey.Span.SequenceEqual(first.MinimumKey.Span) ||
                                    !chunk.MaximumKey.Span.SequenceEqual(first.MaximumKey.Span) ||
                                    chunk.Filter.HashCount != first.Filter.HashCount ||
                                    chunk.Filter.BitCount != first.Filter.BitCount))
            {
                throw Corrupt("Append/merge run chunks are incomplete or inconsistent.");
            }

            byte[] filter = chunks.SelectMany(chunk => chunk.Filter.Bits.ToArray()).ToArray();
            if (chunks.SelectMany(chunk => chunk.Extents).Any() is false ||
                filter.Length * (long)BitsPerByte < first.Filter.BitCount)
            {
                throw Corrupt("Append/merge run chunks contain incomplete ownership or filter data.");
            }

            runs.Add(new(first.Generation, first.EntryCount,
                chunks.SelectMany(chunk => chunk.Extents).ToArray(), first.MinimumKey,
                first.MaximumKey, new(first.Filter.HashCount, first.Filter.BitCount, filter)));
        }
        return new(runs, retired)
        {
            RunBranches = runPages.Where(item => !mixedRuns.Contains(item.Key))
            .ToDictionary(item => item.Key, item => (IReadOnlyList<PhysicalPageId>)item.Value.ToArray())
        };
    }

    private static IEnumerable<byte[]> EncodeRun(AppendMergeRunDescriptor run)
    {
        int extentChunks = (run.Extents.Count + MaximumExtentsPerRunRecord - 1) /
                           MaximumExtentsPerRunRecord;
        int filterChunks = (run.Filter.Bits.Length + MaximumFilterBytesPerRunRecord - 1) /
                           MaximumFilterBytesPerRunRecord;
        int chunkCount = Math.Max(extentChunks, filterChunks);
        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            yield return EncodeRunChunk(run, chunkIndex, chunkCount,
                run.Extents.Skip(chunkIndex * MaximumExtentsPerRunRecord)
                    .Take(MaximumExtentsPerRunRecord).ToArray(),
                run.Filter.Bits.Slice(Math.Min(chunkIndex * MaximumFilterBytesPerRunRecord,
                        run.Filter.Bits.Length),
                    Math.Min(MaximumFilterBytesPerRunRecord,
                        Math.Max(0, run.Filter.Bits.Length - chunkIndex * MaximumFilterBytesPerRunRecord))));
        }
    }

    private static byte[] EncodeRunChunk(AppendMergeRunDescriptor run, int chunkIndex,
        int chunkCount, IReadOnlyList<PhysicalPageExtent> extents, ReadOnlyMemory<byte> filterChunk)
    {
        byte[] minimum = run.MinimumKey.ToArray();
        byte[] maximum = run.MaximumKey.ToArray();
        byte[] filter = filterChunk.ToArray();
        byte[] result = new byte[checked(RunHeaderLength + minimum.Length + maximum.Length +
            filter.Length + extents.Count * ExtentLength)];
        result[0] = RunRecordKind;
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(sizeof(byte)), run.Generation);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(sizeof(byte) + sizeof(long)), run.EntryCount);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(sizeof(byte) + sizeof(long) * 2), chunkIndex);
        int offset = sizeof(byte) + sizeof(long) * 2 + sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), chunkCount);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), extents.Count);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), minimum.Length);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), maximum.Length);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), run.Filter.HashCount);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), run.Filter.BitCount);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), filter.Length);
        offset = RunHeaderLength;
        minimum.CopyTo(result.AsSpan(offset));
        offset += minimum.Length;
        maximum.CopyTo(result.AsSpan(offset));
        offset += maximum.Length;
        filter.CopyTo(result.AsSpan(offset));
        offset += filter.Length;
        foreach (PhysicalPageExtent extent in extents)
        {
            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(offset), extent.FirstPage.Value);
            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(offset + sizeof(long)), extent.PageCount);
            offset += ExtentLength;
        }
        return result;
    }

    private static RunFragment DecodeRun(ReadOnlySpan<byte> record)
    {
        if (record.Length < RunHeaderLength || record[0] != RunRecordKind)
        {
            throw Corrupt("Run descriptor is invalid.");
        }

        long generation = BinaryPrimitives.ReadInt64LittleEndian(record[sizeof(byte)..]);
        long entryCount = BinaryPrimitives.ReadInt64LittleEndian(record[(sizeof(byte) + sizeof(long))..]);
        int chunkIndex = BinaryPrimitives.ReadInt32LittleEndian(record[(sizeof(byte) + sizeof(long) * 2)..]);
        int offset = sizeof(byte) + sizeof(long) * 2 + sizeof(int);
        int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(record[offset..]);
        offset += sizeof(int);
        int extentCount = BinaryPrimitives.ReadInt32LittleEndian(record[offset..]);
        offset += sizeof(int);
        int minimumLength = BinaryPrimitives.ReadInt32LittleEndian(record[offset..]);
        offset += sizeof(int);
        int maximumLength = BinaryPrimitives.ReadInt32LittleEndian(record[offset..]);
        offset += sizeof(int);
        int hashCount = BinaryPrimitives.ReadInt32LittleEndian(record[offset..]);
        offset += sizeof(int);
        int bitCount = BinaryPrimitives.ReadInt32LittleEndian(record[offset..]);
        offset += sizeof(int);
        int filterLength = BinaryPrimitives.ReadInt32LittleEndian(record[offset..]);
        long expectedLength = RunHeaderLength + (long)minimumLength + maximumLength + filterLength +
            extentCount * (long)ExtentLength;
        if (generation <= 0 || entryCount <= 0 || chunkIndex < 0 || chunkCount <= 0 ||
            chunkIndex >= chunkCount || extentCount < 0 || extentCount > MaximumExtentsPerRunRecord || minimumLength <= 0 ||
            maximumLength <= 0 || hashCount <= 0 || hashCount > AppendMergeOptions.MaximumFilterHashCount ||
            bitCount < BitsPerByte || filterLength < 0 || record.Length != expectedLength)
        {
            throw Corrupt("Run descriptor bounds are invalid.");
        }

        offset = RunHeaderLength;
        byte[] minimum = record.Slice(offset, minimumLength).ToArray();
        offset += minimumLength;
        byte[] maximum = record.Slice(offset, maximumLength).ToArray();
        offset += maximumLength;
        byte[] filter = record.Slice(offset, filterLength).ToArray();
        offset += filterLength;
        var extents = new PhysicalPageExtent[extentCount];
        try
        {
            for (int index = 0; index < extentCount; index++)
            {
                extents[index] = new(new(BinaryPrimitives.ReadInt64LittleEndian(record[offset..])),
                    BinaryPrimitives.ReadInt64LittleEndian(record[(offset + sizeof(long))..]));
                offset += ExtentLength;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new PhysicalPageCorruptionException("Run descriptor ownership is invalid.");
        }
        return new(generation, entryCount, chunkIndex, chunkCount, extents, minimum, maximum,
            new(hashCount, bitCount, filter));
    }

    private static IEnumerable<byte[]> EncodeRetired(AppendMergeRetiredPages retired)
    {
        // Each fragment is independently retired at the same visibility boundary. Keeping
        // the existing record format also lets recovery and reclamation consume it directly.
        foreach (PhysicalPageExtent[] extents in retired.Extents.Chunk(MaximumRetiredExtentsPerRecord))
        {
            yield return EncodeRetiredChunk(new(retired.LastVisibleGeneration, extents));
        }
    }

    private static byte[] EncodeRetiredChunk(AppendMergeRetiredPages retired)
    {
        byte[] result = new byte[RetiredHeaderLength + retired.Extents.Count * ExtentLength];
        result[0] = RetiredRecordKind;
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(sizeof(byte)), retired.LastVisibleGeneration);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(sizeof(byte) + sizeof(long)), retired.Extents.Count);
        int offset = RetiredHeaderLength;
        foreach (PhysicalPageExtent extent in retired.Extents)
        {
            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(offset), extent.FirstPage.Value);
            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(offset + sizeof(long)), extent.PageCount);
            offset += ExtentLength;
        }
        return result;
    }

    private static AppendMergeRetiredPages DecodeRetired(ReadOnlySpan<byte> record)
    {
        if (record.Length < RetiredHeaderLength || record[0] != RetiredRecordKind)
        {
            throw Corrupt("Retired append/merge extent record is invalid.");
        }

        long generation = BinaryPrimitives.ReadInt64LittleEndian(record[sizeof(byte)..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(record[(sizeof(byte) + sizeof(long))..]);
        if (generation <= 0 || count <= 0 || record.Length != RetiredHeaderLength + count * (long)ExtentLength)
        {
            throw Corrupt("Retired append/merge extent bounds are invalid.");
        }

        var extents = new PhysicalPageExtent[count];
        int offset = RetiredHeaderLength;
        try
        {
            for (int index = 0; index < count; index++)
            {
                extents[index] = new(new(BinaryPrimitives.ReadInt64LittleEndian(record[offset..])),
                    BinaryPrimitives.ReadInt64LittleEndian(record[(offset + sizeof(long))..]));
                offset += ExtentLength;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new PhysicalPageCorruptionException("Retired append/merge ownership is invalid.");
        }
        return new(generation, extents);
    }

    private static SlottedDataPage NewPage(IPhysicalPageAllocator allocation, long generation) =>
        new(PhysicalPageFileOptions.DefaultPageSize, allocation.AllocatePage(), generation);

    private static PhysicalPageCorruptionException Corrupt(string message) => new(message);

    private sealed record RunFragment(long Generation, long EntryCount, int ChunkIndex,
        int ChunkCount, IReadOnlyList<PhysicalPageExtent> Extents, ReadOnlyMemory<byte> MinimumKey,
        ReadOnlyMemory<byte> MaximumKey, AppendMergeKeyFilter Filter);
}
