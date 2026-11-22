using System.Buffers.Binary;

namespace EntityForge.Db.Storage.Pages;

internal sealed class OrderedPageTreeCodec<TKey, TValue> where TKey : notnull
{
    private const byte CatalogRootRecord = 1;
    private const byte LeafMetadataRecord = 2;
    private const byte LeafEntryRecord = 3;
    private const byte CatalogDescriptorRecord = 4;
    private const byte FreePageDescriptorRecord = 5;
    private const int CatalogHeaderSize = 1 + sizeof(long) + 16 + sizeof(int);
    private const int DescriptorHeaderSize = sizeof(long) + sizeof(int);
    private const int LeafMetadataSize = 1 + sizeof(long) * 2;
    private const int EntryHeaderSize = 1 + sizeof(int) * 2;
    private readonly IOrderedIndexCodec<TKey> _keyCodec;
    private readonly IOrderedIndexCodec<TValue> _valueCodec;

    public OrderedPageTreeCodec(IOrderedIndexCodec<TKey> keyCodec, IOrderedIndexCodec<TValue> valueCodec)
    {
        _keyCodec = keyCodec ?? throw new ArgumentNullException(nameof(keyCodec));
        _valueCodec = valueCodec ?? throw new ArgumentNullException(nameof(valueCodec));
    }

    public OrderedCatalogImage EncodeCatalog(Guid accessPathId, long generation,
        IReadOnlyList<OrderedLeafDescriptor<TKey>> leaves, IReadOnlyList<PhysicalPageId> reusableBranches,
        IReadOnlyCollection<PhysicalPageId> freePages,
        ref long nextPageId, long pageLsn)
    {
        var branches = new List<KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>>>();
        SlottedDataPage? branch = null;
        int branchIndex = 0;
        IEnumerable<byte[]> records = leaves.Select(EncodeLeafDescriptor)
            .Concat(freePages.OrderBy(item => item.Value).Select(EncodeFreePageDescriptor));
        foreach (byte[] descriptor in records)
        {
            branch ??= new(PhysicalPageFileOptions.DefaultPageSize,
                branchIndex < reusableBranches.Count ? reusableBranches[branchIndex++] : new(nextPageId++), generation);
            if (branch.TryInsert(descriptor, pageLsn, out _))
            {
                continue;
            }

            if (branch.SlotCount == 0)
            {
                throw new PhysicalPageCapacityException("An ordered catalog descriptor exceeds page capacity.");
            }

            branches.Add(new(branch.PageId, branch.EncodeOwned()));
            branch = new(PhysicalPageFileOptions.DefaultPageSize,
                branchIndex < reusableBranches.Count ? reusableBranches[branchIndex++] : new(nextPageId++), generation);
            if (!branch.TryInsert(descriptor, pageLsn, out _))
            {
                throw new PhysicalPageCapacityException("An ordered catalog descriptor exceeds page capacity.");
            }
        }
        if (branch is not null)
        {
            branches.Add(new(branch.PageId, branch.EncodeOwned()));
        }

        var root = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, new(0), generation);
        byte[] record = new byte[CatalogHeaderSize + branches.Count * sizeof(long)];
        record[0] = CatalogRootRecord;
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(1), generation);
        accessPathId.TryWriteBytes(record.AsSpan(1 + sizeof(long), 16));
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(1 + sizeof(long) + 16), branches.Count);
        int offset = CatalogHeaderSize;
        foreach (KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>> item in branches)
        {
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset), item.Key.Value);
            offset += sizeof(long);
        }
        if (!root.TryInsert(record, pageLsn, out _))
        {
            throw new OrderedPageTreeException("The ordered access-path catalog exceeds one selected-format page.");
        }

        return new(root.EncodeOwned(), branches, branches.Select(item => item.Key).ToArray());
    }

    public OrderedCatalogRoot DecodeCatalogRoot(ReadOnlySpan<byte> image)
    {
        SlottedDataPage page = SlottedDataPage.Decode(image, new(0));
        if (page.OccupiedRecords.Count != 1)
        {
            throw Corrupt("Catalog record count is invalid.");
        }

        ReadOnlySpan<byte> record = page.OccupiedRecords[0].Span;
        if (record.Length < CatalogHeaderSize || record[0] != CatalogRootRecord)
        {
            throw Corrupt("Catalog header is invalid.");
        }

        long generation = BinaryPrimitives.ReadInt64LittleEndian(record[1..]);
        Guid accessPathId = new(record.Slice(1 + sizeof(long), 16));
        int count = BinaryPrimitives.ReadInt32LittleEndian(record[(1 + sizeof(long) + 16)..]);
        if (generation < 0 || accessPathId == Guid.Empty || count < 0)
        {
            throw Corrupt("Catalog values are invalid.");
        }

        if (record.Length != CatalogHeaderSize + count * (long)sizeof(long))
        {
            throw Corrupt("Catalog root bounds are invalid.");
        }

        var branches = new List<PhysicalPageId>(count);
        int offset = CatalogHeaderSize;
        for (int index = 0; index < count; index++)
        {
            branches.Add(new(BinaryPrimitives.ReadInt64LittleEndian(record[offset..])));
            offset += sizeof(long);
        }
        return new(accessPathId, new(generation), branches);
    }

    public IReadOnlyList<OrderedLeafDescriptor<TKey>> DecodeCatalogBranch(
        ReadOnlySpan<byte> image, PhysicalPageId pageId)
        => DecodeCatalogBranchState(image, pageId).Leaves;

    public OrderedCatalogBranchState<TKey> DecodeCatalogBranchState(
        ReadOnlySpan<byte> image, PhysicalPageId pageId)
    {
        SlottedDataPage page = SlottedDataPage.Decode(image, pageId);
        var leaves = new List<OrderedLeafDescriptor<TKey>>(page.OccupiedRecords.Count);
        var freePages = new List<PhysicalPageId>();
        foreach (ReadOnlyMemory<byte> item in page.OccupiedRecords)
        {
            ReadOnlySpan<byte> record = item.Span;
            if (record.Length == 1 + sizeof(long) && record[0] == FreePageDescriptorRecord)
            {
                freePages.Add(new(BinaryPrimitives.ReadInt64LittleEndian(record[1..])));
                continue;
            }
            if (record.Length < 1 + DescriptorHeaderSize || record[0] != CatalogDescriptorRecord)
            {
                throw Corrupt("Catalog descriptor header is invalid.");
            }

            var leafId = new PhysicalPageId(BinaryPrimitives.ReadInt64LittleEndian(record[1..]));
            int keyLength = BinaryPrimitives.ReadInt32LittleEndian(record[(1 + sizeof(long))..]);
            if (keyLength < 0 || 1 + DescriptorHeaderSize + keyLength != record.Length)
            {
                throw Corrupt("Catalog descriptor bounds are invalid.");
            }

            leaves.Add(new(leafId, _keyCodec.Decode(record[(1 + DescriptorHeaderSize)..])));
        }
        return new(leaves, freePages);
    }

    private byte[] EncodeLeafDescriptor(OrderedLeafDescriptor<TKey> leaf)
    {
        byte[] key = _keyCodec.Encode(leaf.FirstKey);
        byte[] descriptor = new byte[1 + DescriptorHeaderSize + key.Length];
        descriptor[0] = CatalogDescriptorRecord;
        BinaryPrimitives.WriteInt64LittleEndian(descriptor.AsSpan(1), leaf.PageId.Value);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor.AsSpan(1 + sizeof(long)), key.Length);
        key.CopyTo(descriptor.AsSpan(1 + DescriptorHeaderSize));
        return descriptor;
    }

    private static byte[] EncodeFreePageDescriptor(PhysicalPageId pageId)
    {
        byte[] descriptor = new byte[1 + sizeof(long)];
        descriptor[0] = FreePageDescriptorRecord;
        BinaryPrimitives.WriteInt64LittleEndian(descriptor.AsSpan(1), pageId.Value);
        return descriptor;
    }

    public byte[] EncodeLeaf(PhysicalPageId pageId, long generation, PhysicalPageId? previous,
        PhysicalPageId? next, IReadOnlyList<OrderedLeafEntry<TKey, TValue>> entries, long pageLsn)
    {
        var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, pageId, generation);
        byte[] metadata = new byte[LeafMetadataSize];
        metadata[0] = LeafMetadataRecord;
        BinaryPrimitives.WriteInt64LittleEndian(metadata.AsSpan(1), previous?.Value ?? -1);
        BinaryPrimitives.WriteInt64LittleEndian(metadata.AsSpan(1 + sizeof(long)), next?.Value ?? -1);
        if (!page.TryInsert(metadata, pageLsn, out _))
        {
            throw new InvalidOperationException();
        }

        foreach (OrderedLeafEntry<TKey, TValue> entry in entries)
        {
            byte[] key = _keyCodec.Encode(entry.Key);
            byte[] value = _valueCodec.Encode(entry.Value);
            byte[] record = new byte[EntryHeaderSize + key.Length + value.Length];
            record[0] = LeafEntryRecord;
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(1), key.Length);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(1 + sizeof(int)), value.Length);
            key.CopyTo(record.AsSpan(EntryHeaderSize));
            value.CopyTo(record.AsSpan(EntryHeaderSize + key.Length));
            if (!page.TryInsert(record, pageLsn, out _))
            {
                throw new PhysicalPageCapacityException("Ordered leaf entries exceed selected page capacity.");
            }
        }
        return page.EncodeOwned();
    }

    public OrderedLeaf<TKey, TValue> DecodeLeaf(ReadOnlySpan<byte> image, PhysicalPageId pageId)
    {
        SlottedDataPage page = SlottedDataPage.Decode(image, pageId);
        if (page.OccupiedRecords.Count == 0)
        {
            throw Corrupt("Leaf metadata is missing.");
        }

        ReadOnlySpan<byte> metadata = page.OccupiedRecords[0].Span;
        if (metadata.Length != LeafMetadataSize || metadata[0] != LeafMetadataRecord)
        {
            throw Corrupt("Leaf metadata is invalid.");
        }

        long previous = BinaryPrimitives.ReadInt64LittleEndian(metadata[1..]);
        long next = BinaryPrimitives.ReadInt64LittleEndian(metadata[(1 + sizeof(long))..]);
        var entries = new List<OrderedLeafEntry<TKey, TValue>>(page.OccupiedRecords.Count - 1);
        foreach (ReadOnlyMemory<byte> item in page.OccupiedRecords.Skip(1))
        {
            entries.Add(DecodeEntry(item.Span));
        }

        return new(pageId, page.Generation, previous < 0 ? null : new(previous), next < 0 ? null : new(next), entries);
    }

    public bool Fits(PhysicalPageId pageId, long generation, PhysicalPageId? previous,
        PhysicalPageId? next, IReadOnlyList<OrderedLeafEntry<TKey, TValue>> entries)
    {
        try
        {
            _ = EncodeLeaf(pageId, generation, previous, next, entries, generation);
            return true;
        }
        catch (PhysicalPageCapacityException)
        {
            return false;
        }
    }

    public int LeafEntryStorageBytes(OrderedLeafEntry<TKey, TValue> entry) => checked(
        SlottedDataPage.SlotSize + EntryHeaderSize +
        _keyCodec.Encode(entry.Key).Length + _valueCodec.Encode(entry.Value).Length);

    public static int EmptyLeafStorageBytes =>
        SlottedDataPage.HeaderSize + SlottedDataPage.SlotSize + LeafMetadataSize;

    private OrderedLeafEntry<TKey, TValue> DecodeEntry(ReadOnlySpan<byte> record)
    {
        if (record.Length < EntryHeaderSize || record[0] != LeafEntryRecord)
        {
            throw Corrupt("Leaf entry header is invalid.");
        }

        int keyLength = BinaryPrimitives.ReadInt32LittleEndian(record[1..]);
        int valueLength = BinaryPrimitives.ReadInt32LittleEndian(record[(1 + sizeof(int))..]);
        if (keyLength < 0 || valueLength < 0 || EntryHeaderSize + (long)keyLength + valueLength != record.Length)
        {
            throw Corrupt("Leaf entry bounds are invalid.");
        }

        return new(_keyCodec.Decode(record.Slice(EntryHeaderSize, keyLength)),
            _valueCodec.Decode(record.Slice(EntryHeaderSize + keyLength, valueLength)));
    }

    private static PhysicalPageCorruptionException Corrupt(string message) => new(message);
}

internal sealed record OrderedCatalogRoot(Guid AccessPathId, OrderedPageTreeGeneration Generation,
    IReadOnlyList<PhysicalPageId> Branches);

internal sealed record OrderedCatalogImage(ReadOnlyMemory<byte> Root,
    IReadOnlyList<KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>>> Branches,
    IReadOnlyList<PhysicalPageId> BranchIds);

internal sealed record OrderedLeafDescriptor<TKey>(PhysicalPageId PageId, TKey FirstKey);

internal sealed record OrderedCatalogBranchState<TKey>(IReadOnlyList<OrderedLeafDescriptor<TKey>> Leaves,
    IReadOnlyList<PhysicalPageId> FreePages);

internal sealed record OrderedLeafEntry<TKey, TValue>(TKey Key, TValue Value);

internal sealed record OrderedLeaf<TKey, TValue>(PhysicalPageId PageId, long Generation,
    PhysicalPageId? Previous, PhysicalPageId? Next, IReadOnlyList<OrderedLeafEntry<TKey, TValue>> Entries);
