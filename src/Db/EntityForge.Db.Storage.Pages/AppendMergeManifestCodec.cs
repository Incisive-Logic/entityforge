using System.Buffers.Binary;

namespace EntityForge.Db.Storage.Pages;

internal sealed partial class AppendMergeManifestCodec
{
    private const int BitsPerByte = 8;
    private const int MaximumExtentsPerRunRecord = 256;
    private const int MaximumFilterBytesPerRunRecord = 8 * 1024;
    private const byte RootRecordKind = 52;
    private const byte InlineRetirementRootRecordKind = 55;
    private const byte InlineMetadataRootRecordKind = 56;
    private const byte InlinePayloadRunRecordKind = 57;
    private const byte InlinePayloadRootRecordKind = 58;
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

    internal static int RetiredRecordCount(int extentCount) =>
        checked((int)((extentCount + (long)MaximumRetiredExtentsPerRecord - 1) / MaximumRetiredExtentsPerRecord));

    // One record per page is an upper bound, including fragmented run descriptors.
    // Indirect roots preserve descriptor sharing beyond one page of references.
    public static bool CanSeparateRuns(IReadOnlyList<AppendMergeRunDescriptor> runs, int retiredRecords)
    {
        long branches = retiredRecords;
        foreach (AppendMergeRunDescriptor run in runs)
        {
            branches += Math.Max(
                (run.Extents.Count + (long)MaximumExtentsPerRunRecord - 1) / MaximumExtentsPerRunRecord,
                (run.Filter.Bits.Length + (long)MaximumFilterBytesPerRunRecord - 1) / MaximumFilterBytesPerRunRecord) + OverflowRecordCount(run);
        }

        return branches <= (long)MaximumRootBranches * MaximumIndexBranches;
    }

    public AppendMergeManifestImage Encode(Guid accessPathId, long generation,
        IReadOnlyList<AppendMergeRunDescriptor> runs,
        IReadOnlyList<AppendMergeRetiredPages> retired,
        IPhysicalPageAllocator allocation,
        IReadOnlyDictionary<long, IReadOnlyList<PhysicalPageId>>? reusableRuns = null,
        IReadOnlySet<long>? excludedRuns = null, bool inlineRetirement = false, bool inlineRuns = false)
    {
        var branches = new List<KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>>>();
        var runBranches = new Dictionary<long, IReadOnlyList<PhysicalPageId>>(reusableRuns is null ? 0 : runs.Count);
        var allBranches = new List<PhysicalPageId>();
        SlottedDataPage? branch = null;
        var pages = new List<PhysicalPageId>();
        var inlineRecords = new List<(byte[]? Bytes, AppendMergeRunDescriptor? Run, int Length, byte Kind)>();
        long inlineBytes = 0;
        long retirementBytes = retired.Sum(item => (long)item.Extents.Count * ExtentLength) +
            (long)RetiredRecordCount(retired) * (RetiredHeaderLength + SlottedDataPage.SlotSize);
        long maximumBranches = RetiredRecordCount(retired) + runs.Sum(run => (long)Math.Max(
            (run.Extents.Count + MaximumExtentsPerRunRecord - 1) / MaximumExtentsPerRunRecord,
            (run.Filter.Bits.Length + MaximumFilterBytesPerRunRecord - 1) / MaximumFilterBytesPerRunRecord) + OverflowRecordCount(run));
        long inlineBudget = PhysicalPageFileOptions.DefaultPageSize - SlottedDataPage.HeaderSize -
            SlottedDataPage.SlotSize - RootHeaderLength - maximumBranches * sizeof(long);
        long runBudget = inlineBudget - (retirementBytes <= inlineBudget ? retirementBytes : 0);
        if (reusableRuns is null)
        {
            foreach (var run in runs)
            {
                WriteRun(run);
            }

            WriteRetired();
            FinishGroup();
        }
        else
        {
            foreach (var run in runs)
            {
                if (excludedRuns?.Contains(run.Generation) != true &&
                    reusableRuns.TryGetValue(run.Generation, out var reused))
                {
                    runBranches.Add(run.Generation, reused);
                    allBranches.AddRange(reused);
                    continue;
                }
                WriteRun(run);
                FinishPage();
                var ownedPages = pages.ToArray();
                if (ownedPages.Length != 0)
                {
                    runBranches.Add(run.Generation, ownedPages);
                }

                FinishGroup();
            }
            WriteRetired();
            FinishGroup();
        }

        void FinishPage()
        {
            if (branch is null)
            {
                return;
            }

            pages.Add(branch.PageId);
            branches.Add(new(branch.PageId, branch.EncodeOwned()));
            branch = null;
        }
        void FinishGroup()
        {
            FinishPage();
            allBranches.AddRange(pages);
            pages.Clear();
        }
        Memory<byte> Reserve(int length)
        {
            branch ??= NewPage(allocation, generation);
            if (branch.TryReserve(length, generation, out var destination, out _))
            {
                return destination;
            }

            if (branch.SlotCount == 0)
            {
                throw new PhysicalPageCapacityException("An append/merge run descriptor exceeds page capacity.");
            }

            FinishPage();
            branch = NewPage(allocation, generation);
            if (!branch.TryReserve(length, generation, out destination, out _))
            {
                throw new PhysicalPageCapacityException("An append/merge run descriptor exceeds page capacity.");
            }

            return destination;
        }
        void WriteRun(AppendMergeRunDescriptor run)
        {
            if (run.InlinePayload is { } payload)
            {
                if (run.Extents.Count != 0 || run.OverflowExtents.Count != 0 || run.EntryCount != payload.Records.Count ||
                    run.Filter.Bits.Length > MaximumFilterBytesPerRunRecord)
                {
                    throw Corrupt("Inline run descriptor ownership is invalid.");
                }

                int length = checked(RunHeaderLength + run.MinimumKey.Length + run.MaximumKey.Length +
                    run.Filter.Bits.Length + sizeof(int) + payload.Bytes.Length);
                if (inlineRuns && inlineBytes + length + SlottedDataPage.SlotSize <= runBudget)
                {
                    inlineRecords.Add((null, run, length, InlinePayloadRunRecordKind));
                    inlineBytes += length + SlottedDataPage.SlotSize;
                }
                else
                {
                    WriteInlinePayload(Reserve(length).Span, run);
                }

                return;
            }
            int chunks = Math.Max((run.Extents.Count + MaximumExtentsPerRunRecord - 1) / MaximumExtentsPerRunRecord,
                (run.Filter.Bits.Length + MaximumFilterBytesPerRunRecord - 1) / MaximumFilterBytesPerRunRecord);
            long bytes = (long)chunks * (RunHeaderLength + SlottedDataPage.SlotSize + run.MinimumKey.Length +
                run.MaximumKey.Length) + run.Filter.Bits.Length + (long)run.Extents.Count * ExtentLength;
            // Only a single-page run travels with the mutable manifest root.
            // Multi-page runs have stable, potentially large Bloom filters: keep
            // their descriptors independently reusable instead of logging those
            // filter bytes again whenever an unrelated small run is appended.
            bool inline = run.OverflowExtents.Count == 0 && inlineRuns && inlineRetirement && reusableRuns is not null &&
                run.Extents.Count == 1 && run.Extents[0].PageCount == 1 &&
                inlineBytes + bytes <= runBudget;
            for (int chunk = 0; chunk < chunks; chunk++)
            {
                int first = Math.Min(chunk * MaximumExtentsPerRunRecord, run.Extents.Count);
                int count = Math.Min(MaximumExtentsPerRunRecord, run.Extents.Count - first);
                int filterFirst = Math.Min(chunk * MaximumFilterBytesPerRunRecord, run.Filter.Bits.Length);
                var filter = run.Filter.Bits.Span.Slice(filterFirst,
                    Math.Min(MaximumFilterBytesPerRunRecord, run.Filter.Bits.Length - filterFirst));
                int length = checked(RunHeaderLength + run.MinimumKey.Length + run.MaximumKey.Length + filter.Length + count * ExtentLength);
                if (inline)
                {
                    var record = new byte[length];
                    WriteRunChunk(record, run, chunk, chunks, first, count, filter);
                    inlineRecords.Add((record, null, length, RunRecordKind));
                    inlineBytes += length + SlottedDataPage.SlotSize;
                }
                else
                {
                    WriteRunChunk(Reserve(length).Span, run, chunk, chunks, first, count, filter);
                }
            }
            foreach (byte[] record in EncodeOverflowOwnership(run))
            {
                record.AsMemory().CopyTo(Reserve(record.Length));
            }
        }
        void WriteRetired()
        {
            // Root space is otherwise padding. Store the complete retirement list
            // there when it fits; fall back atomically to ordinary branches when it
            // does not. Keep run descriptors independently shareable.
            bool fitsRoot = inlineRetirement && reusableRuns is not null && retired.Count != 0 &&
                RootHeaderLength + (long)allBranches.Count * sizeof(long) + SlottedDataPage.SlotSize +
                retirementBytes + inlineBytes <= PhysicalPageFileOptions.DefaultPageSize - SlottedDataPage.HeaderSize;
            foreach (var item in retired)
            {
                for (int first = 0; first < item.Extents.Count; first += MaximumRetiredExtentsPerRecord)
                {
                    int count = Math.Min(MaximumRetiredExtentsPerRecord, item.Extents.Count - first);
                    int length = RetiredHeaderLength + count * ExtentLength;
                    byte[]? inline = fitsRoot ? new byte[length] : null;
                    var record = inline is null ? Reserve(length).Span : inline.AsSpan();
                    record[0] = RetiredRecordKind;
                    BinaryPrimitives.WriteInt64LittleEndian(record[sizeof(byte)..], item.LastVisibleGeneration);
                    BinaryPrimitives.WriteInt32LittleEndian(record[(sizeof(byte) + sizeof(long))..], count);
                    WriteExtents(record[RetiredHeaderLength..], item.Extents, first, count);
                    if (inline is not null)
                    {
                        inlineRecords.Add((inline, null, length, RetiredRecordKind));
                    }
                }
            }
        }
        bool indirect = allBranches.Count > MaximumRootBranches;
        IReadOnlyList<PhysicalPageId> rootBranches = indirect
            ? WriteBranchIndex(allBranches, branches, generation, allocation) : allBranches;
        if (indirect && inlineRecords.Count != 0)
        {
            throw new PhysicalPageCapacityException("An indirect manifest cannot contain inline records.");
        }
        PhysicalPageId rootPageId = allocation.AllocatePage();
        var root = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, rootPageId, generation);
        if (!root.TryReserve(checked(RootHeaderLength + rootBranches.Count * sizeof(long)), generation, out var rootMemory, out _))
        {
            throw new PhysicalPageCapacityException("The append/merge manifest root exceeds page capacity.");
        }

        var rootRecord = rootMemory.Span;
        rootRecord[0] = indirect ? IndirectRootRecordKind : inlineRecords.Count == 0 ? RootRecordKind :
            inlineRecords.Any(record => record.Kind == InlinePayloadRunRecordKind) ? InlinePayloadRootRecordKind :
            inlineRecords.Any(record => record.Kind == RunRecordKind) ? InlineMetadataRootRecordKind : InlineRetirementRootRecordKind;
        accessPathId.TryWriteBytes(rootRecord.Slice(sizeof(byte), 16));
        BinaryPrimitives.WriteInt64LittleEndian(rootRecord[(sizeof(byte) + 16)..], generation);
        BinaryPrimitives.WriteInt32LittleEndian(rootRecord[(sizeof(byte) + 16 + sizeof(long))..], rootBranches.Count);
        int offset = RootHeaderLength;
        foreach (PhysicalPageId page in rootBranches)
        {
            BinaryPrimitives.WriteInt64LittleEndian(rootRecord[offset..], page.Value);
            offset += sizeof(long);
        }
        foreach (var record in inlineRecords)
        {
            if (!root.TryReserve(record.Length, generation, out var destination, out _))
            {
                throw new PhysicalPageCapacityException("Inline manifest retirement exceeds root capacity.");
            }

            if (record.Run is { } run)
            {
                WriteInlinePayload(destination.Span, run);
            }
            else
            {
                record.Bytes!.AsSpan().CopyTo(destination.Span);
            }
        }
        return new(rootPageId, root.EncodeOwned(), branches)
        {
            RunBranches = runBranches,
            AllBranches = indirect ? allBranches.Concat(rootBranches).ToArray() : allBranches.ToArray()
        };
    }

    public AppendMergeManifestRoot DecodeRoot(ReadOnlySpan<byte> image, PhysicalPageId pageId)
    {
        SlottedDataPage page = SlottedDataPage.Decode(image, pageId);
        if (page.OccupiedRecords.Count == 0)
        {
            throw Corrupt("Append/merge manifest root count is invalid.");
        }

        ReadOnlySpan<byte> record = page.OccupiedRecords[0].Span;
        if (record.Length < RootHeaderLength || record[0] is not (RootRecordKind or InlineRetirementRootRecordKind or InlineMetadataRootRecordKind or InlinePayloadRootRecordKind or IndirectRootRecordKind))
        {
            throw Corrupt("Append/merge manifest root is invalid.");
        }

        bool inlineMetadata = record[0] is InlineMetadataRootRecordKind or InlinePayloadRootRecordKind;
        bool inlineRetirement = record[0] is not (RootRecordKind or IndirectRootRecordKind);
        if ((!inlineRetirement && page.OccupiedRecords.Count != 1) ||
            (inlineRetirement && page.OccupiedRecords.Count < 2))
        {
            throw Corrupt("Append/merge manifest root retirement count is invalid.");
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

        var metadata = inlineMetadata ? DecodeBranches([(pageId, (ReadOnlyMemory<byte>)image.ToArray())], allowRootRecord: true) : null;
        return new(accessPathId, generation, branches)
        {
            IndirectBranches = record[0] == IndirectRootRecordKind,
            InlineRuns = metadata?.Runs ?? [],
            InlineRetired = metadata?.Retired ?? (inlineRetirement
                ? page.OccupiedRecords.Skip(1).Select(item => DecodeRetired(item.Span)).ToArray() : [])
        };
    }

    public AppendMergeManifestBranch DecodeBranches(
        IEnumerable<(PhysicalPageId PageId, ReadOnlyMemory<byte> Image)> branches, bool allowRootRecord = false)
    {
        var runFragments = new List<RunFragment>();
        var overflow = new Dictionary<long, List<OverflowOwnershipChunk>>();
        var retired = new List<AppendMergeRetiredPages>();
        var runPages = new Dictionary<long, List<PhysicalPageId>>();
        var mixedRuns = new HashSet<long>();
        foreach ((PhysicalPageId pageId, ReadOnlyMemory<byte> image) in branches)
        {
            var generations = new HashSet<long>();
            bool hasRetired = false;
            foreach (ReadOnlyMemory<byte> item in SlottedDataPage.ReadOccupiedRecords(image, pageId))
            {
                if (allowRootRecord && item.Span.Length != 0 && item.Span[0] is InlineMetadataRootRecordKind or InlinePayloadRootRecordKind)
                {
                    hasRetired = true;
                    continue;
                } // Root contents are rewritten, never independently reused.
                if (item.Span.Length != 0 && item.Span[0] == RetiredRecordKind)
                {
                    retired.Add(DecodeRetired(item.Span));
                    hasRetired = true;
                }
                else if (item.Span.Length != 0 && item.Span[0] == OverflowOwnershipKind)
                {
                    var chunk = DecodeOverflowOwnership(item.Span);
                    if (!overflow.TryGetValue(chunk.Generation, out var parts))
                    {
                        overflow.Add(chunk.Generation, parts = []);
                    }
                    parts.Add(chunk);
                    generations.Add(chunk.Generation);
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
                                    chunk.EntryCount != first.EntryCount || chunk.HasOverflow != first.HasOverflow ||
                                    !chunk.MinimumKey.Span.SequenceEqual(first.MinimumKey.Span) ||
                                    !chunk.MaximumKey.Span.SequenceEqual(first.MaximumKey.Span) ||
                                    chunk.Filter.HashCount != first.Filter.HashCount ||
                                    chunk.Filter.BitCount != first.Filter.BitCount))
            {
                throw Corrupt("Append/merge run chunks are incomplete or inconsistent.");
            }

            byte[] filter = chunks.SelectMany(chunk => chunk.Filter.Bits.ToArray()).ToArray();
            if ((first.InlinePayload is null && chunks.SelectMany(chunk => chunk.Extents).Any() is false) ||
                (chunks.Any(chunk => chunk.InlinePayload is not null) &&
                    (chunks.Length != 1 || first.InlinePayload is null || first.Extents.Count != 0)) ||
                filter.Length * (long)BitsPerByte < first.Filter.BitCount)
            {
                throw Corrupt("Append/merge run chunks contain incomplete ownership or filter data.");
            }

            var run = new AppendMergeRunDescriptor(first.Generation, first.EntryCount,
                chunks.SelectMany(chunk => chunk.Extents).ToArray(), first.MinimumKey,
                first.MaximumKey, new(first.Filter.HashCount, first.Filter.BitCount, filter))
            {
                InlinePayload = first.InlinePayload,
                OverflowExtents = CombineOverflowOwnership(first.Generation, first.HasOverflow, overflow)
            };
            ValidateRunOwnership(run);
            runs.Add(run);
        }
        if (overflow.Count != 0)
        {
            throw Corrupt("Overflow ownership has no corresponding run.");
        }
        return new(runs, retired)
        {
            RunBranches = runPages.Where(item => !mixedRuns.Contains(item.Key))
            .ToDictionary(item => item.Key, item => (IReadOnlyList<PhysicalPageId>)item.Value.ToArray())
        };
    }

    private static void WriteInlinePayload(Span<byte> destination, AppendMergeRunDescriptor run)
    {
        var payload = run.InlinePayload!;
        WriteRunChunk(destination, run, 0, 1, 0, 0, run.Filter.Bits.Span);
        destination[0] = InlinePayloadRunRecordKind;
        int offset = destination.Length - payload.Bytes.Length - sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], payload.Bytes.Length);
        payload.Bytes.Span.CopyTo(destination[(offset + sizeof(int))..]);
    }

    private static void WriteRunChunk(Span<byte> result, AppendMergeRunDescriptor run, int chunkIndex,
        int chunkCount, int firstExtent, int extentCount, ReadOnlySpan<byte> filter)
    {
        var minimum = run.MinimumKey.Span;
        var maximum = run.MaximumKey.Span;
        result[0] = run.OverflowExtents.Count == 0 ? RunRecordKind : OverflowRunRecordKind;
        BinaryPrimitives.WriteInt64LittleEndian(result.Slice(sizeof(byte)), run.Generation);
        BinaryPrimitives.WriteInt64LittleEndian(result.Slice(sizeof(byte) + sizeof(long)), run.EntryCount);
        BinaryPrimitives.WriteInt32LittleEndian(result.Slice(sizeof(byte) + sizeof(long) * 2), chunkIndex);
        int offset = sizeof(byte) + sizeof(long) * 2 + sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.Slice(offset), chunkCount);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.Slice(offset), extentCount);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.Slice(offset), minimum.Length);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.Slice(offset), maximum.Length);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.Slice(offset), run.Filter.HashCount);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.Slice(offset), run.Filter.BitCount);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.Slice(offset), filter.Length);
        offset = RunHeaderLength;
        minimum.CopyTo(result.Slice(offset));
        offset += minimum.Length;
        maximum.CopyTo(result.Slice(offset));
        offset += maximum.Length;
        filter.CopyTo(result.Slice(offset));
        offset += filter.Length;
        WriteExtents(result[offset..], run.Extents, firstExtent, extentCount);
    }

    private static void WriteExtents(Span<byte> destination, IReadOnlyList<PhysicalPageExtent> extents, int first, int count)
    {
        for (int index = 0; index < count; index++)
        {
            var extent = extents[first + index];
            BinaryPrimitives.WriteInt64LittleEndian(destination[(index * ExtentLength)..], extent.FirstPage.Value);
            BinaryPrimitives.WriteInt64LittleEndian(destination[(index * ExtentLength + sizeof(long))..], extent.PageCount);
        }
    }

    private static RunFragment DecodeRun(ReadOnlySpan<byte> record)
    {
        if (record.Length < RunHeaderLength || record[0] is not (RunRecordKind or InlinePayloadRunRecordKind or OverflowRunRecordKind))
        {
            throw Corrupt("Run descriptor is invalid.");
        }

        bool inlinePayload = record[0] == InlinePayloadRunRecordKind;
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
        AppendMergeInlinePayload? payload = null;
        if (inlinePayload)
        {
            if (extentCount != 0 || chunkIndex != 0 || chunkCount != 1 || expectedLength < RunHeaderLength ||
                expectedLength > record.Length - sizeof(int))
            {
                throw Corrupt("Inline run descriptor bounds are invalid.");
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(record[(int)expectedLength..]);
            if (length < 0 || expectedLength + sizeof(int) + length != record.Length)
            {
                throw Corrupt("Inline run payload length is invalid.");
            }

            payload = AppendMergeInlinePayload.Decode(record.Slice((int)expectedLength + sizeof(int), length), entryCount);
            expectedLength += sizeof(int) + length;
        }
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
            new(hashCount, bitCount, filter))
        {
            InlinePayload = payload,
            HasOverflow = record[0] == OverflowRunRecordKind
        };
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
        ReadOnlyMemory<byte> MaximumKey, AppendMergeKeyFilter Filter)
    {
        public bool HasOverflow
        {
            get; init;
        }

        public AppendMergeInlinePayload? InlinePayload
        {
            get; init;
        }
    }
}

internal sealed record AppendMergeRunDescriptor(long Generation, long EntryCount,
    IReadOnlyList<PhysicalPageExtent> Extents, ReadOnlyMemory<byte> MinimumKey,
    ReadOnlyMemory<byte> MaximumKey, AppendMergeKeyFilter Filter)
{
    public IReadOnlyList<PhysicalPageExtent> OverflowExtents { get; init; } = [];

    public IReadOnlyList<PhysicalPageExtent> OwnedExtents => OverflowExtents.Count == 0 ? Extents : Extents.Concat(OverflowExtents).ToArray();

    public IEnumerable<PhysicalPageId> OwnedPages => OwnedExtents.SelectMany(PagesIn);

    private static IEnumerable<PhysicalPageId> PagesIn(PhysicalPageExtent extent)
    {
        for (long offset = 0; offset < extent.PageCount; offset++)
        {
            yield return new(extent.FirstPage.Value + offset);
        }
    }

    public AppendMergeInlinePayload? InlinePayload
    {
        get; init;
    }

    public IReadOnlyList<PhysicalPageId> Pages { get; } = ExpandPages(Extents);

    private static IReadOnlyList<PhysicalPageId> ExpandPages(
        IReadOnlyList<PhysicalPageExtent> extents)
    {
        int count = checked((int)extents.Sum(extent => extent.PageCount));
        if (count == 0)
        {
            return Array.Empty<PhysicalPageId>();
        }

        var pages = new PhysicalPageId[count];
        int index = 0;
        foreach (PhysicalPageExtent extent in extents)
        {
            for (long offset = 0; offset < extent.PageCount; offset++)
            {
                pages[index++] = new(extent.FirstPage.Value + offset);
            }
        }

        return Array.AsReadOnly(pages);
    }
}

internal sealed record AppendMergeManifestRoot(Guid AccessPathId, long Generation,
    IReadOnlyList<PhysicalPageId> Branches)
{
    public bool IndirectBranches
    {
        get; init;
    }

    public IReadOnlyList<PhysicalPageId> IndexPages { get; init; } = [];

    public IReadOnlyList<AppendMergeRetiredPages> InlineRetired { get; init; } = [];

    public IReadOnlyList<AppendMergeRunDescriptor> InlineRuns { get; init; } = [];
}

internal sealed record AppendMergeManifestImage(PhysicalPageId RootPageId, ReadOnlyMemory<byte> Root,
    IReadOnlyList<KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>>> Branches)
{
    public IReadOnlyDictionary<long, IReadOnlyList<PhysicalPageId>> RunBranches
    {
        get; init;
    }
    =
        new Dictionary<long, IReadOnlyList<PhysicalPageId>>();

    public IReadOnlyList<PhysicalPageId> AllBranches { get; init; } = [];
}

internal sealed record AppendMergeManifestBranch(IReadOnlyList<AppendMergeRunDescriptor> Runs,
    IReadOnlyList<AppendMergeRetiredPages> Retired)
{
    public IReadOnlyDictionary<long, IReadOnlyList<PhysicalPageId>> RunBranches
    {
        get; init;
    }
    =
        new Dictionary<long, IReadOnlyList<PhysicalPageId>>();
}

internal sealed record AppendMergeRetiredPages(long LastVisibleGeneration,
    IReadOnlyList<PhysicalPageExtent> Extents)
{
    public IEnumerable<PhysicalPageId> Pages => Extents.SelectMany(extent => PagesIn(extent));

    private static IEnumerable<PhysicalPageId> PagesIn(PhysicalPageExtent extent)
    {
        for (long offset = 0; offset < extent.PageCount; offset++)
        {
            yield return new(extent.FirstPage.Value + offset);
        }
    }
}
