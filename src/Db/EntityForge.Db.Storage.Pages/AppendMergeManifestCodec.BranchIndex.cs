using System.Buffers.Binary;

namespace EntityForge.Db.Storage.Pages;

internal sealed partial class AppendMergeManifestCodec
{
    private const byte IndirectRootRecordKind = 59;
    private const byte BranchIndexRecordKind = 60;
    private const int IndexHeaderLength = sizeof(byte) + sizeof(int);
    private const int MaximumRootBranches = (PhysicalPageFileOptions.DefaultPageSize -
        SlottedDataPage.HeaderSize - SlottedDataPage.SlotSize - RootHeaderLength) / sizeof(long);

    private const int MaximumIndexBranches = (PhysicalPageFileOptions.DefaultPageSize -
        SlottedDataPage.HeaderSize - SlottedDataPage.SlotSize - IndexHeaderLength) / sizeof(long);

    private static IReadOnlyList<PhysicalPageId> WriteBranchIndex(IReadOnlyList<PhysicalPageId> leaves,
        List<KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>>> images, long generation, IPhysicalPageAllocator allocation)
    {
        int count = checked((int)((leaves.Count + (long)MaximumIndexBranches - 1) / MaximumIndexBranches));
        if (count > MaximumRootBranches)
        {
            throw new PhysicalPageCapacityException("The append/merge manifest index exceeds its two-level bound.");
        }
        var result = new PhysicalPageId[count];
        for (int group = 0; group < count; group++)
        {
            int first = group * MaximumIndexBranches;
            int length = Math.Min(MaximumIndexBranches, leaves.Count - first);
            PhysicalPageId id = allocation.AllocatePage();
            var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, id, generation);
            if (!page.TryReserve(IndexHeaderLength + length * sizeof(long), generation, out var memory, out _))
            {
                throw new PhysicalPageCapacityException("Manifest branch index exceeds page capacity.");
            }
            var record = memory.Span;
            record[0] = BranchIndexRecordKind;
            BinaryPrimitives.WriteInt32LittleEndian(record[1..], length);
            for (int index = 0; index < length; index++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(record[(IndexHeaderLength + index * sizeof(long))..], leaves[first + index].Value);
            }
            result[group] = id;
            images.Add(new(id, page.EncodeOwned()));
        }
        return result;
    }

    internal async ValueTask<AppendMergeManifestRoot> ResolveBranchIndexAsync(AppendMergeManifestRoot root,
        PhysicalPageId rootPage, Func<PhysicalPageId, ValueTask<ReadOnlyMemory<byte>>> read,
        Func<PhysicalPageId, bool> owns, CancellationToken cancellationToken = default)
    {
        if (!root.IndirectBranches)
        {
            return root;
        }
        if (root.Branches.Count == 0 || root.Branches.Contains(rootPage))
        {
            throw Corrupt("Manifest branch index root is empty or cyclic.");
        }
        var seen = root.Branches.ToHashSet();
        seen.Add(rootPage);
        var leaves = new List<PhysicalPageId>();
        foreach (PhysicalPageId indexPage in root.Branches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!owns(indexPage))
            {
                throw Corrupt("Manifest branch index is outside owned page extents.");
            }
            SlottedDataPage page = SlottedDataPage.Decode((await read(indexPage).ConfigureAwait(false)).Span, indexPage);
            if (page.Generation != root.Generation || page.OccupiedRecords.Count != 1)
            {
                throw Corrupt("Manifest branch index generation or record count is invalid.");
            }
            ReadOnlyMemory<byte> record = page.OccupiedRecords[0];
            if (record.Length < IndexHeaderLength || record.Span[0] != BranchIndexRecordKind)
            {
                throw Corrupt("Manifest branch index record is invalid.");
            }
            int count = BinaryPrimitives.ReadInt32LittleEndian(record.Span[1..]);
            if (count <= 0 || count > MaximumIndexBranches || record.Length != IndexHeaderLength + count * (long)sizeof(long))
            {
                throw Corrupt("Manifest branch index bounds are invalid.");
            }
            for (int index = 0; index < count; index++)
            {
                var leaf = new PhysicalPageId(BinaryPrimitives.ReadInt64LittleEndian(record.Span[(IndexHeaderLength + index * sizeof(long))..]));
                if (leaf.Value <= 0 || !seen.Add(leaf) || !owns(leaf))
                {
                    throw Corrupt("Manifest branch index contains duplicate, cyclic or unowned pages.");
                }
                leaves.Add(leaf);
            }
        }
        return root with
        {
            Branches = leaves,
            IndexPages = root.Branches,
            IndirectBranches = false
        };
    }
}
