using System.Collections;
using System.Collections.Immutable;

namespace EntityForge.Db.Storage.Local;

// Immutable catalog snapshots share full blocks. Keeping value descriptors in
// small arrays avoids a descriptor object and an AVL node for every change.
internal sealed class JournalSegmentList : IReadOnlyList<ChangeJournalSegmentDescriptor>
{
    private const int BlockSize = 128;
    private readonly ImmutableList<ChangeJournalSegmentDescriptor[]> _blocks;

    public int Count
    {
        get;
    }

    private JournalSegmentList(ImmutableList<ChangeJournalSegmentDescriptor[]> blocks, int count)
    {
        _blocks = blocks;
        Count = count;
    }

    internal static JournalSegmentList From(IEnumerable<ChangeJournalSegmentDescriptor> segments) =>
        segments as JournalSegmentList ?? new JournalSegmentList([], 0).AddRange(segments);

    internal JournalSegmentList AddRange(IEnumerable<ChangeJournalSegmentDescriptor> segments)
    {
        var blocks = _blocks.ToBuilder();
        int count = Count;
        ChangeJournalSegmentDescriptor[]? writable = null;
        foreach (var segment in segments)
        {
            int offset = count % BlockSize;
            if (offset == 0)
            {
                writable = new ChangeJournalSegmentDescriptor[BlockSize];
                blocks.Add(writable);
            }
            else if (writable is null)
            {
                writable = (ChangeJournalSegmentDescriptor[])blocks[^1].Clone();
                blocks[^1] = writable;
            }
            writable![offset] = segment;
            count = checked(count + 1);
        }
        return count == Count ? this : new(blocks.ToImmutable(), count);
    }

    public ChangeJournalSegmentDescriptor this[int index] => (uint)index < (uint)Count
        ? _blocks[index / BlockSize][index % BlockSize]
        : throw new ArgumentOutOfRangeException(nameof(index));

    public IEnumerator<ChangeJournalSegmentDescriptor> GetEnumerator()
    {
        int remaining = Count;
        foreach (var block in _blocks)
        {
            int length = Math.Min(BlockSize, remaining);
            for (int i = 0; i < length; i++)
            {
                yield return block[i];
            }

            remaining -= length;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
