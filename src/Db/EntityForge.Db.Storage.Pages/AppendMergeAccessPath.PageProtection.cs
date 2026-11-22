namespace EntityForge.Db.Storage.Pages;

public sealed partial class AppendMergeAccessPath<TKey, TValue> where TKey : notnull
{
    // Long maintenance readers must not quarantine every later retired generation.
    // Capture immutable extents, not individual page IDs or the state's retired history.
    private readonly Dictionary<long, ExactReference> _exactReferences = [];

    private void AddExactReference(State state)
    {
        if (_exactReferences.TryGetValue(state.Generation, out var existing))
        {
            existing.Count++;
        }
        else
        {
            var extents = state.Runs.SelectMany(run => run.OwnedExtents)
                .Concat(state.ManifestPages.Select(page => new PhysicalPageExtent(page, 1)))
                .OrderBy(extent => extent.FirstPage.Value).ToArray();
            _exactReferences.Add(state.Generation, new(extents));
        }
        _maintenanceStatus = null;
    }

    private void ReleaseExactReference(long generation)
    {
        lock (_gate)
        {
            if (--_exactReferences[generation].Count == 0)
            {
                _exactReferences.Remove(generation);
            }

            _maintenanceStatus = null;
        }
    }

    private bool IsExactlyProtected(AppendMergeRetiredPages retired)
    {
        foreach (var reference in _exactReferences.Values)
        {
            foreach (var extent in retired.Extents)
            {
                // Source extents are disjoint and sorted. Find the first whose end
                // is after this retirement range's start, then check intersection.
                int low = 0, high = reference.Extents.Length;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    if (reference.Extents[middle].EndExclusive <= extent.FirstPage.Value)
                    {
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle;
                    }
                }
                if (low < reference.Extents.Length && reference.Extents[low].FirstPage.Value < extent.EndExclusive)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private sealed class ExactReference(PhysicalPageExtent[] extents)
    {
        public PhysicalPageExtent[] Extents { get; } = extents;

        public int Count { get; set; } = 1;
    }
}
