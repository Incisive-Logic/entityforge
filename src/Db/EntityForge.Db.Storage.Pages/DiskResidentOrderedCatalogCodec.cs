using System.Buffers.Binary;

namespace EntityForge.Db.Storage.Pages;

internal sealed class DiskResidentOrderedCatalogCodec<TKey, TValue> where TKey : notnull
{
    private const byte RootRecordKind = 11;
    private const byte LeafRecordKind = 12;
    private const byte RetiredRecordKind = 14;
    private const int RootHeaderLength = sizeof(byte) + sizeof(long) + sizeof(long) + 16 + sizeof(int);
    private const int LeafHeaderLength = sizeof(byte) + sizeof(long) + sizeof(int) * 3;
    private readonly IOrderedIndexCodec<TKey> _keys;
    private readonly IOrderedIndexCodec<TValue> _values;

    public DiskResidentOrderedCatalogCodec(IOrderedIndexCodec<TKey> keys, IOrderedIndexCodec<TValue> values)
    {
        _keys = keys;
        _values = values;
    }

    public DiskResidentCatalogImage Encode(Guid accessPathId, long generation, long entryCount,
        IReadOnlyList<DiskResidentLeafDescriptor<TKey, TValue>> leaves,
        IReadOnlyCollection<DiskResidentRetiredPage> retiredPages,
        IPhysicalPageAllocator allocation,
        IReadOnlyDictionary<PhysicalPageId, DiskResidentCatalogBranch<TKey, TValue>>? reusable = null)
    {
        // The root references both reused and new branches; only new branches need page images.
        var writtenBranches = new List<KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>>>();
        var branchIds = new List<PhysicalPageId>();
        var reusableBranchesByFirstLeaf = new Dictionary<PhysicalPageId, (PhysicalPageId Id, DiskResidentCatalogBranch<TKey, TValue> Branch)>();
        if (reusable is not null)
        {
            foreach (var pair in reusable)
            {
                if (pair.Value.Leaves.Count != 0 && pair.Value.RetiredPages.Count == 0)
                {
                    reusableBranchesByFirstLeaf.Add(pair.Value.Leaves[0].PageId, (pair.Key, pair.Value));
                }
            }
        }

        SlottedDataPage? branch = null;
        void FinishBranch()
        {
            if (branch is null)
            {
                return;
            }

            writtenBranches.Add(new(branch.PageId, branch.EncodeOwned()));
            branchIds.Add(branch.PageId);
            branch = null;
        }
        void AppendRecord(byte[] record)
        {
            branch ??= new(PhysicalPageFileOptions.DefaultPageSize, allocation.AllocatePage(), generation);
            if (branch.TryInsert(record, generation, out _))
            {
                return;
            }

            if (branch.SlotCount == 0)
            {
                throw new PhysicalPageCapacityException("A disk-resident catalog descriptor exceeds page capacity.");
            }

            FinishBranch();
            branch = new(PhysicalPageFileOptions.DefaultPageSize, allocation.AllocatePage(), generation);
            if (!branch.TryInsert(record, generation, out _))
            {
                throw new PhysicalPageCapacityException("A disk-resident catalog descriptor exceeds page capacity.");
            }
        }
        for (int index = 0; index < leaves.Count;)
        {
            if (reusableBranchesByFirstLeaf.TryGetValue(leaves[index].PageId, out var prior) &&
                MatchesLeafSequence(leaves, index, prior.Branch.Leaves))
            {
                FinishBranch();
                branchIds.Add(prior.Id);
                index += prior.Branch.Leaves.Count;
            }
            else
            {
                AppendRecord(EncodeLeaf(leaves[index]));
                index++;
            }
        }
        FinishBranch();
        // Keep retirement records separate so changing reclamation debt does not rewrite leaf descriptors.
        foreach (var retired in retiredPages.OrderBy(item => item.PageId.Value))
        {
            AppendRecord(EncodeRetired(retired));
        }

        FinishBranch();
        PhysicalPageId rootPageId = allocation.AllocatePage();
        byte[] rootImage = EncodeRoot(rootPageId, accessPathId, generation, entryCount, branchIds);
        return new(rootPageId, rootImage, writtenBranches, branchIds);
    }

    private static bool MatchesLeafSequence(IReadOnlyList<DiskResidentLeafDescriptor<TKey, TValue>> leaves,
        int startIndex, IReadOnlyList<DiskResidentLeafDescriptor<TKey, TValue>> previousLeaves)
    {
        if (previousLeaves.Count > leaves.Count - startIndex)
        {
            return false;
        }

        for (int offset = 0; offset < previousLeaves.Count; offset++)
        {
            if (!Equals(previousLeaves[offset], leaves[startIndex + offset]))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] EncodeRoot(PhysicalPageId rootPageId, Guid accessPathId,
        long generation, long entryCount, IReadOnlyList<PhysicalPageId> branchIds)
    {
        var root = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, rootPageId, generation);
        byte[] rootRecord = new byte[RootHeaderLength + branchIds.Count * sizeof(long)];
        rootRecord[0] = RootRecordKind;
        BinaryPrimitives.WriteInt64LittleEndian(rootRecord.AsSpan(1), generation);
        BinaryPrimitives.WriteInt64LittleEndian(rootRecord.AsSpan(1 + sizeof(long)), entryCount);
        accessPathId.TryWriteBytes(rootRecord.AsSpan(1 + sizeof(long) * 2, 16));
        BinaryPrimitives.WriteInt32LittleEndian(rootRecord.AsSpan(1 + sizeof(long) * 2 + 16), branchIds.Count);
        int offset = RootHeaderLength;
        foreach (PhysicalPageId id in branchIds)
        {
            BinaryPrimitives.WriteInt64LittleEndian(rootRecord.AsSpan(offset), id.Value);
            offset += sizeof(long);
        }
        if (!root.TryInsert(rootRecord, generation, out _))
        {
            throw new PhysicalPageCapacityException("The disk-resident catalog root exceeds page capacity.");
        }

        return root.EncodeOwned();
    }

    public DiskResidentCatalogRoot DecodeRoot(ReadOnlySpan<byte> image, PhysicalPageId rootPageId)
    {
        SlottedDataPage page = SlottedDataPage.Decode(image, rootPageId);
        if (page.OccupiedRecords.Count != 1)
        {
            throw Corrupt("Catalog root record count is invalid.");
        }

        ReadOnlySpan<byte> record = page.OccupiedRecords[0].Span;
        if (record.Length < RootHeaderLength || record[0] != RootRecordKind)
        {
            throw Corrupt("Catalog root is invalid.");
        }

        long generation = BinaryPrimitives.ReadInt64LittleEndian(record[1..]);
        long count = BinaryPrimitives.ReadInt64LittleEndian(record[(1 + sizeof(long))..]);
        Guid id = new(record.Slice(1 + sizeof(long) * 2, 16));
        int branchCount = BinaryPrimitives.ReadInt32LittleEndian(record[(1 + sizeof(long) * 2 + 16)..]);
        if (generation < 0 || count < 0 || id == Guid.Empty || branchCount < 0 ||
            record.Length != RootHeaderLength + branchCount * (long)sizeof(long))
        {
            throw Corrupt("Catalog bounds are invalid.");
        }

        var branches = new PhysicalPageId[branchCount];
        int offset = RootHeaderLength;
        for (int index = 0; index < branchCount; index++)
        {
            branches[index] = new(BinaryPrimitives.ReadInt64LittleEndian(record[offset..]));
            offset += sizeof(long);
        }
        return new(id, new(generation), count, branches);
    }

    public DiskResidentCatalogBranch<TKey, TValue> DecodeBranch(ReadOnlySpan<byte> image, PhysicalPageId pageId)
    {
        SlottedDataPage page = SlottedDataPage.Decode(image, pageId);
        var leaves = new List<DiskResidentLeafDescriptor<TKey, TValue>>();
        var retired = new List<DiskResidentRetiredPage>();
        foreach (ReadOnlyMemory<byte> item in page.OccupiedRecords)
        {
            ReadOnlySpan<byte> record = item.Span;
            if (record.Length == sizeof(byte) + sizeof(long) * 2 && record[0] == RetiredRecordKind)
            {
                retired.Add(new(new(BinaryPrimitives.ReadInt64LittleEndian(record[1..])),
                    BinaryPrimitives.ReadInt64LittleEndian(record[(1 + sizeof(long))..])));
                continue;
            }
            if (record.Length < LeafHeaderLength || record[0] != LeafRecordKind)
            {
                throw Corrupt("Leaf descriptor is invalid.");
            }

            long id = BinaryPrimitives.ReadInt64LittleEndian(record[1..]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(record[(1 + sizeof(long))..]);
            int keyLength = BinaryPrimitives.ReadInt32LittleEndian(record[(1 + sizeof(long) + sizeof(int))..]);
            int valueLength = BinaryPrimitives.ReadInt32LittleEndian(record[(1 + sizeof(long) + sizeof(int) * 2)..]);
            if (id <= 0 || count <= 0 || keyLength < 0 || valueLength < 0 ||
                record.Length != LeafHeaderLength + (long)keyLength + valueLength)
            {
                throw Corrupt("Leaf descriptor bounds are invalid.");
            }

            leaves.Add(new(new(id), _keys.Decode(record.Slice(LeafHeaderLength, keyLength)),
                _values.Decode(record.Slice(LeafHeaderLength + keyLength, valueLength)), count));
        }
        return new(leaves, retired);
    }

    private byte[] EncodeLeaf(DiskResidentLeafDescriptor<TKey, TValue> descriptor)
    {
        byte[] key = _keys.Encode(descriptor.FirstKey);
        byte[] value = _values.Encode(descriptor.FirstValue);
        byte[] result = new byte[LeafHeaderLength + key.Length + value.Length];
        result[0] = LeafRecordKind;
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(1), descriptor.PageId.Value);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(1 + sizeof(long)), descriptor.EntryCount);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(1 + sizeof(long) + sizeof(int)), key.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(1 + sizeof(long) + sizeof(int) * 2), value.Length);
        key.CopyTo(result.AsSpan(LeafHeaderLength));
        value.CopyTo(result.AsSpan(LeafHeaderLength + key.Length));
        return result;
    }

    private static byte[] EncodeRetired(DiskResidentRetiredPage retired)
    {
        byte[] result = new byte[sizeof(byte) + sizeof(long) * 2];
        result[0] = RetiredRecordKind;
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(1), retired.PageId.Value);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(1 + sizeof(long)), retired.LastGeneration);
        return result;
    }

    private static PhysicalPageCorruptionException Corrupt(string message) => new(message);
}

internal sealed record DiskResidentCatalogRoot(Guid AccessPathId, OrderedPageTreeGeneration Generation,
    long EntryCount, IReadOnlyList<PhysicalPageId> Branches);

internal sealed record DiskResidentCatalogBranch<TKey, TValue>(
    IReadOnlyList<DiskResidentLeafDescriptor<TKey, TValue>> Leaves,
    IReadOnlyList<DiskResidentRetiredPage> RetiredPages) where TKey : notnull;

internal sealed record DiskResidentRetiredPage(PhysicalPageId PageId, long LastGeneration);

internal sealed record DiskResidentCatalogImage(PhysicalPageId RootPageId, ReadOnlyMemory<byte> Root,
    IReadOnlyList<KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>>> Branches,
    IReadOnlyList<PhysicalPageId> BranchIds);
