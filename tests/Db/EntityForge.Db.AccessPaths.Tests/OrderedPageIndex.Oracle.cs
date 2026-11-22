namespace EntityForge.Db.AccessPaths;

/// <summary>
/// A profile-neutral ordered, duplicate-preserving access path. Keys and values form the unique
/// composite identity, allowing equality, range and directional adjacency to share one primitive.
/// </summary>
internal sealed partial class OrderedPageIndex<TKey, TValue> where TKey : notnull
{
    private readonly IComparer<TKey> _keyComparer;
    private readonly IComparer<TValue> _valueComparer;
    private readonly OrderedPageIndexOptions _options;
    private readonly Guid _accessPathId = Guid.NewGuid();
    private Node _root;
    private long _generation;

    public OrderedPageIndex(IComparer<TKey>? keyComparer = null, IComparer<TValue>? valueComparer = null,
        OrderedPageIndexOptions? options = null)
    {
        _keyComparer = keyComparer ?? Comparer<TKey>.Default;
        _valueComparer = valueComparer ?? Comparer<TValue>.Default;
        _options = options ?? new();
        _root = new LeafNode();
    }

    public int Count
    {
        get; private set;
    }

    public int PageCount => CountNodes(_root);

    public bool Add(TKey key, TValue value)
    {
        Split? split = Insert(_root, new(key, value));
        if (split is not null)
        {
            _root = new BranchNode([_root, split.Right]);
        }

        bool added = split?.Added ?? _lastInsertAdded;
        if (added)
        {
            _generation = checked(_generation + 1);
        }

        return added;
    }

    private bool _lastInsertAdded;

    public bool Remove(TKey key, TValue value)
    {
        var target = new Entry(key, value);
        var path = new List<PathStep>();
        Node node = _root;
        while (node is BranchNode branch)
        {
            int childIndex = branch.ChildIndex(target, this);
            path.Add(new(branch, childIndex));
            node = branch.Children[childIndex];
        }
        var leaf = (LeafNode)node;
        int position = leaf.Entries.BinarySearch(target, new EntryComparer(this));
        if (position < 0)
        {
            return false;
        }

        leaf.Entries.RemoveAt(position);
        Count--;
        if (path.Count != 0 && leaf.Entries.Count < MinimumLeafEntries)
        {
            RebalanceLeaf(leaf, path);
        }

        _generation = checked(_generation + 1);
        return true;
    }

    public IReadOnlyList<TValue> Find(TKey key)
    {
        OrderedAccessPathTelemetry.Seek();
        var matches = new List<TValue>();
        for (LeafNode? leaf = LowerBoundLeaf(key); leaf is not null; leaf = leaf.Next)
        {
            foreach (Entry entry in leaf.Entries)
            {
                int comparison = _keyComparer.Compare(entry.Key, key);
                if (comparison > 0)
                {
                    return matches;
                }

                if (comparison == 0)
                {
                    matches.Add(entry.Value);
                }
            }
        }
        return matches;
    }

    public OrderedIndexPage<TKey, TValue> ReadPage(int pageSize,
        OrderedIndexRange<TKey>? range = null,
        OrderedScanDirection direction = OrderedScanDirection.Forward,
        OrderedIndexCursor<TKey, TValue>? cursor = null)
    {
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        range ??= OrderedIndexRange<TKey>.All;
        IEnumerable<Entry> query = RangeEntries(range, direction);
        if (cursor is not null)
        {
            if (cursor.AccessPathId != _accessPathId || cursor.Generation != _generation)
            {
                throw new ArgumentException("A cursor belongs to a different access-path snapshot.", nameof(cursor));
            }

            if (cursor.Direction != direction)
            {
                throw new ArgumentException("A cursor cannot change scan direction.", nameof(cursor));
            }

            var after = new Entry(cursor.Key, cursor.Value);
            query = query.Where(item => direction is OrderedScanDirection.Forward
                ? Compare(item, after) > 0 : Compare(item, after) < 0);
        }
        Entry[] selected = query.Take(checked(pageSize + 1)).ToArray();
        bool hasMore = selected.Length > pageSize;
        Entry[] visible = hasMore ? selected[..pageSize] : selected;
        OrderedIndexCursor<TKey, TValue>? next = hasMore && visible.Length != 0
            ? new(_accessPathId, _generation, visible[^1].Key, visible[^1].Value, direction) : null;
        OrderedAccessPathTelemetry.Page(visible.Length);
        return new(visible.Select(item => new KeyValuePair<TKey, TValue>(item.Key, item.Value)).ToArray(), next);
    }

    public IReadOnlyList<KeyValuePair<TKey, TValue>> Snapshot() => Entries()
        .Select(item => new KeyValuePair<TKey, TValue>(item.Key, item.Value)).ToArray();

    public static OrderedPageIndex<TKey, TValue> Rebuild(
        IEnumerable<KeyValuePair<TKey, TValue>> entries, IComparer<TKey>? keyComparer = null,
        IComparer<TValue>? valueComparer = null, OrderedPageIndexOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var index = new OrderedPageIndex<TKey, TValue>(keyComparer, valueComparer, options);
        foreach (KeyValuePair<TKey, TValue> entry in entries)
        {
            index.Add(entry.Key, entry.Value);
        }

        return index;
    }

    private Split? Insert(Node node, Entry entry)
    {
        if (node is LeafNode leaf)
        {
            int position = leaf.Entries.BinarySearch(entry, new EntryComparer(this));
            if (position >= 0)
            {
                _lastInsertAdded = false;
                return null;
            }
            leaf.Entries.Insert(~position, entry);
            Count++;
            _lastInsertAdded = true;
            if (leaf.Entries.Count <= _options.LeafCapacity)
            {
                return null;
            }

            int midpoint = leaf.Entries.Count / 2;
            var right = new LeafNode();
            right.Entries.AddRange(leaf.Entries.GetRange(midpoint, leaf.Entries.Count - midpoint));
            leaf.Entries.RemoveRange(midpoint, leaf.Entries.Count - midpoint);
            right.Next = leaf.Next;
            if (right.Next is not null)
            {
                right.Next.Previous = right;
            }

            leaf.Next = right;
            right.Previous = leaf;
            OrderedAccessPathTelemetry.Split();
            return new(right, true);
        }
        var branch = (BranchNode)node;
        int childIndex = branch.ChildIndex(entry, this);
        Split? childSplit = Insert(branch.Children[childIndex], entry);
        if (childSplit is null)
        {
            return null;
        }

        branch.Children.Insert(childIndex + 1, childSplit.Right);
        if (branch.Children.Count <= _options.BranchCapacity)
        {
            return null;
        }

        int branchMidpoint = branch.Children.Count / 2;
        var rightBranch = new BranchNode(branch.Children.GetRange(branchMidpoint,
            branch.Children.Count - branchMidpoint));
        branch.Children.RemoveRange(branchMidpoint, branch.Children.Count - branchMidpoint);
        OrderedAccessPathTelemetry.Split();
        return new(rightBranch, childSplit.Added);
    }

    private int MinimumLeafEntries => (_options.LeafCapacity + 1) / 2;

    private int MinimumBranchChildren => (_options.BranchCapacity + 1) / 2;

    private void RebalanceLeaf(LeafNode leaf, IReadOnlyList<PathStep> path)
    {
        PathStep step = path[^1];
        BranchNode parent = step.Parent;
        LeafNode? left = step.ChildIndex > 0 ? (LeafNode)parent.Children[step.ChildIndex - 1] : null;
        LeafNode? right = step.ChildIndex + 1 < parent.Children.Count
            ? (LeafNode)parent.Children[step.ChildIndex + 1] : null;
        if (left is not null && left.Entries.Count > MinimumLeafEntries)
        {
            leaf.Entries.Insert(0, left.Entries[^1]);
            left.Entries.RemoveAt(left.Entries.Count - 1);
            return;
        }
        if (right is not null && right.Entries.Count > MinimumLeafEntries)
        {
            leaf.Entries.Add(right.Entries[0]);
            right.Entries.RemoveAt(0);
            return;
        }
        if (left is not null)
        {
            left.Entries.AddRange(leaf.Entries);
            left.Next = leaf.Next;
            if (leaf.Next is not null)
            {
                leaf.Next.Previous = left;
            }

            parent.Children.RemoveAt(step.ChildIndex);
            OrderedAccessPathTelemetry.Merge();
        }
        else if (right is not null)
        {
            leaf.Entries.AddRange(right.Entries);
            leaf.Next = right.Next;
            if (right.Next is not null)
            {
                right.Next.Previous = leaf;
            }

            parent.Children.RemoveAt(step.ChildIndex + 1);
            OrderedAccessPathTelemetry.Merge();
        }
        RebalanceBranches(path);
    }

    private void RebalanceBranches(IReadOnlyList<PathStep> path)
    {
        for (int level = path.Count - 1; level >= 0; level--)
        {
            BranchNode branch = path[level].Parent;
            if (ReferenceEquals(branch, _root))
            {
                if (branch.Children.Count == 1)
                {
                    _root = branch.Children[0];
                }

                return;
            }
            if (branch.Children.Count >= MinimumBranchChildren)
            {
                return;
            }

            PathStep parentStep = path[level - 1];
            BranchNode parent = parentStep.Parent;
            int index = parentStep.ChildIndex;
            BranchNode? left = index > 0 ? (BranchNode)parent.Children[index - 1] : null;
            BranchNode? right = index + 1 < parent.Children.Count
                ? (BranchNode)parent.Children[index + 1] : null;
            if (left is not null && left.Children.Count > MinimumBranchChildren)
            {
                branch.Children.Insert(0, left.Children[^1]);
                left.Children.RemoveAt(left.Children.Count - 1);
                return;
            }
            if (right is not null && right.Children.Count > MinimumBranchChildren)
            {
                branch.Children.Add(right.Children[0]);
                right.Children.RemoveAt(0);
                return;
            }
            if (left is not null)
            {
                left.Children.AddRange(branch.Children);
                parent.Children.RemoveAt(index);
                OrderedAccessPathTelemetry.Merge();
            }
            else if (right is not null)
            {
                branch.Children.AddRange(right.Children);
                parent.Children.RemoveAt(index + 1);
                OrderedAccessPathTelemetry.Merge();
            }
        }
    }

    private IEnumerable<Entry> Entries()
    {
        Node node = _root;
        while (node is BranchNode branch)
        {
            node = branch.Children[0];
        }

        for (LeafNode? leaf = (LeafNode)node; leaf is not null; leaf = leaf.Next)
        {
            foreach (Entry entry in leaf.Entries)
            {
                yield return entry;
            }
        }
    }

    private LeafNode LowerBoundLeaf(TKey key)
    {
        Node node = _root;
        while (node is BranchNode branch)
        {
            int index = 0;
            while (index + 1 < branch.Children.Count &&
                   _keyComparer.Compare(branch.Children[index + 1].First.Key, key) < 0)
            {
                index++;
            }

            node = branch.Children[index];
        }
        return (LeafNode)node;
    }

    private LeafNode UpperBoundLeaf(TKey key)
    {
        Node node = _root;
        while (node is BranchNode branch)
        {
            int index = 0;
            while (index + 1 < branch.Children.Count &&
                   _keyComparer.Compare(branch.Children[index + 1].First.Key, key) <= 0)
            {
                index++;
            }

            node = branch.Children[index];
        }
        return (LeafNode)node;
    }

    private IEnumerable<Entry> RangeEntries(OrderedIndexRange<TKey> range, OrderedScanDirection direction)
    {
        if (direction is OrderedScanDirection.Forward)
        {
            LeafNode? leaf = range.HasLowerBound ? LowerBoundLeaf(range.LowerInclusive!) : FirstLeaf();
            for (; leaf is not null; leaf = leaf.Next)
            {
                foreach (Entry entry in leaf.Entries)
                {
                    if (range.HasLowerBound && _keyComparer.Compare(entry.Key, range.LowerInclusive!) < 0)
                    {
                        continue;
                    }

                    if (range.HasUpperBound && _keyComparer.Compare(entry.Key, range.UpperInclusive!) > 0)
                    {
                        yield break;
                    }

                    yield return entry;
                }
            }

            yield break;
        }
        LeafNode? reverseLeaf = range.HasUpperBound ? UpperBoundLeaf(range.UpperInclusive!) : LastLeaf();
        for (; reverseLeaf is not null; reverseLeaf = reverseLeaf.Previous)
        {
            for (int index = reverseLeaf.Entries.Count - 1; index >= 0; index--)
            {
                Entry entry = reverseLeaf.Entries[index];
                if (range.HasUpperBound && _keyComparer.Compare(entry.Key, range.UpperInclusive!) > 0)
                {
                    continue;
                }

                if (range.HasLowerBound && _keyComparer.Compare(entry.Key, range.LowerInclusive!) < 0)
                {
                    yield break;
                }

                yield return entry;
            }
        }
    }

    private LeafNode FirstLeaf()
    {
        Node node = _root;
        while (node is BranchNode branch)
        {
            node = branch.Children[0];
        }

        return (LeafNode)node;
    }

    private LeafNode LastLeaf()
    {
        Node node = _root;
        while (node is BranchNode branch)
        {
            node = branch.Children[^1];
        }

        return (LeafNode)node;
    }

    private int Compare(Entry left, Entry right)
    {
        int key = _keyComparer.Compare(left.Key, right.Key);
        return key != 0 ? key : _valueComparer.Compare(left.Value, right.Value);
    }

    private static int CountNodes(Node node) => node is LeafNode ? 1 :
        1 + ((BranchNode)node).Children.Sum(CountNodes);

    private abstract class Node
    {
        public abstract Entry First
        {
            get;
        }
    }

    private sealed class LeafNode : Node
    {
        public List<Entry> Entries { get; } = [];

        public LeafNode? Previous
        {
            get; set;
        }

        public LeafNode? Next
        {
            get; set;
        }

        public override Entry First => Entries[0];
    }

    private sealed class BranchNode(List<Node> children) : Node
    {
        public List<Node> Children { get; } = children;

        public override Entry First => Children[0].First;

        public int ChildIndex(Entry entry, OrderedPageIndex<TKey, TValue> owner)
        {
            int index = 0;
            while (index + 1 < Children.Count && owner.Compare(entry, Children[index + 1].First) >= 0)
            {
                index++;
            }

            return index;
        }
    }

    private sealed record Split(Node Right, bool Added);

    private sealed record PathStep(BranchNode Parent, int ChildIndex);

    private sealed record Entry(TKey Key, TValue Value);

    private sealed class EntryComparer(OrderedPageIndex<TKey, TValue> owner) : IComparer<Entry>
    {
        public int Compare(Entry? x, Entry? y) => owner.Compare(x!, y!);
    }
}
