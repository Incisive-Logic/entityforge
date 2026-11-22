namespace EntityForge.Db.Storage.Pages;

/// <summary>Tracks reusable physical pages as coalesced ranges rather than one object per free page.</summary>
internal sealed class PhysicalFreePageRanges
{
    private sealed record Range(long First, long End);

    private readonly SortedSet<Range> _ranges = new(Comparer<Range>.Create(
        (left, right) => left.First.CompareTo(right.First)));

    private readonly SortedSet<Range> _bySize = new(Comparer<Range>.Create((left, right) =>
    {
        int size = (right.End - right.First).CompareTo(left.End - left.First);
        return size != 0 ? size : left.First.CompareTo(right.First);
    }));

    private long _pageCount;

    public static PhysicalFreePageRanges Reconstruct(PhysicalPageSpaceDirectory published,
        IReadOnlyCollection<PhysicalPageId> directoryBranchPages, long physicalPageCount)
    {
        var result = new PhysicalFreePageRanges();
        if (physicalPageCount <= PhysicalPageSpaceDirectory.FirstAccessMethodPageId)
        {
            return result;
        }

        (long First, long End)[] occupied = published.Entries.SelectMany(entry => entry.Extents)
            .Select(extent => (extent.FirstPage.Value, extent.EndExclusive))
            .Concat(directoryBranchPages.Select(page => (page.Value, checked(page.Value + 1))))
            .Where(range => range.Item2 > PhysicalPageSpaceDirectory.FirstAccessMethodPageId &&
                            range.Item1 < physicalPageCount)
            .OrderBy(range => range.Item1).ToArray();
        long next = PhysicalPageSpaceDirectory.FirstAccessMethodPageId;
        foreach ((long first, long end) in occupied)
        {
            long boundedFirst = Math.Max(first, PhysicalPageSpaceDirectory.FirstAccessMethodPageId);
            if (boundedFirst > next)
            {
                result.AddRange(next, boundedFirst);
            }

            next = Math.Max(next, Math.Min(end, physicalPageCount));
        }
        if (next < physicalPageCount)
        {
            result.AddRange(next, physicalPageCount);
        }

        return result;
    }

    internal long? FirstPage => _ranges.Min?.First;

    internal void Include(PhysicalFreePageRanges other)
    {
        if (ReferenceEquals(this, other))
        {
            return;
        }

        foreach (var range in other._ranges)
        {
            AddRange(range.First, range.End);
        }
    }

    internal long PageCount => _pageCount;

    public bool TryTake(long? preferredPageId, bool preferLowest, out long pageId)
    {
        if (_ranges.Count == 0)
        {
            pageId = default;
            return false;
        }
        Range selected;
        if (preferredPageId is { } preferred && _ranges.TryGetValue(new(preferred, preferred), out var preferredRange))
        {
            selected = preferredRange;
        }
        else if (preferLowest)
        {
            selected = _ranges.Min!;
        }
        else
        {
            selected = _bySize.Min!;
        }

        pageId = selected.First;
        Remove(selected);
        if (pageId + 1 < selected.End)
        {
            Insert(new(pageId + 1, selected.End));
        }

        return true;
    }

    public void Add(long pageId)
    {
        if (pageId < PhysicalPageSpaceDirectory.FirstAccessMethodPageId)
        {
            throw new ArgumentOutOfRangeException(nameof(pageId));
        }

        AddRange(pageId, checked(pageId + 1));
    }

    private void AddRange(long first, long end)
    {
        if (first >= end)
        {
            return;
        }
        // Predecessor/successor lookups avoid scanning every free range for
        // each page released after a replacement. Both indexes change together.
        Range? previous = _ranges.GetViewBetween(new(long.MinValue, 0), new(first, first)).Max;
        if (previous is { } left && left.End >= first)
        {
            first = left.First;
            end = Math.Max(end, left.End);
            Remove(left);
        }

        while (_ranges.GetViewBetween(new(first, first), new(long.MaxValue, long.MaxValue)).Min is { } following &&
               following.First <= end)
        {
            end = Math.Max(end, following.End);
            Remove(following);
        }
        Insert(new(first, end));
    }

    private void Insert(Range range)
    {
        _ranges.Add(range);
        _bySize.Add(range);
        _pageCount = checked(_pageCount + (range.End - range.First));
    }

    private void Remove(Range range)
    {
        _ranges.Remove(range);
        _bySize.Remove(range);
        _pageCount -= range.End - range.First;
    }
}
