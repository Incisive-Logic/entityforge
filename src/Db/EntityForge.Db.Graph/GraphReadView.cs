using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;

namespace EntityForge.Db.Graph;

internal readonly record struct GraphAdjacencyKey(GraphVertexId Vertex, string Label);

internal sealed partial record GraphReadView(
    IReadOnlyDictionary<GraphVertexId, GraphVertex> Vertices,
    IReadOnlyDictionary<GraphEdgeId, GraphEdge> Edges)
{
    private const int MaximumPropertyValueCacheEntries = 4_096;
    private const int MaximumPropertyPostingIndexes = 32;
    private const int MaximumPropertyPostingEntries = 1_000_000;
    private const int PropertyPostingIndexAdmissionLookups = 8;
    // Query caches belong to this immutable generation; point reads and writes do not allocate them.
    private GraphAdjacencyIndexes? _adjacency;
    private Lazy<bool>? _completeCandidateAdjacency;

    internal bool HasCompleteCandidateAdjacency => LazyInitializer.EnsureInitialized(ref _completeCandidateAdjacency,
        () => new Lazy<bool>(() => Edges.Values.All(e => e.StorageRole.HasFlag(
            GraphEdgeStorageRole.OutgoingAdjacency | GraphEdgeStorageRole.IncomingAdjacency)))).Value;

    private IReadOnlyList<GraphVertexId>? _identityOrder;
    private GraphQueryStatistics? _statistics;
    private GraphQueryCache<(string Property, GraphPatternSortDirection Direction),
        IReadOnlyList<GraphVertexId>>? _orderedProperties;

    private GraphQueryCache<(string Property, GraphPatternSortDirection Direction),
        IReadOnlyList<GraphVertexId>> OrderedPropertiesCache =>
        LazyInitializer.EnsureInitialized(ref _orderedProperties, static () => new(value => value.Count));

    private ConcurrentDictionary<(string Property, DatabaseValue Value),
        IReadOnlyList<GraphVertexId>>? _propertyValues;

    private ConcurrentDictionary<(string Property, DatabaseValue Value),
        IReadOnlyList<GraphVertexId>> PropertyValuesCache =>
        LazyInitializer.EnsureInitialized(ref _propertyValues, static () => new());

    private readonly object _propertyLookupGate = new();
    private ConcurrentDictionary<string, int>? _propertyLookupCounts;

    private ConcurrentDictionary<string, int> PropertyLookupCountsCache =>
        LazyInitializer.EnsureInitialized(ref _propertyLookupCounts, static () => new(StringComparer.Ordinal));

    private ConcurrentDictionary<string, Lazy<GraphPropertyPostingIndex>>? _propertyPostingIndexes;

    private ConcurrentDictionary<string, Lazy<GraphPropertyPostingIndex>> PropertyPostingIndexesCache =>
        LazyInitializer.EnsureInitialized(ref _propertyPostingIndexes, static () => new(StringComparer.Ordinal));

    private readonly object _propertyPostingGate = new();
    private int _propertyPostingEntries;
    private readonly ConcurrentDictionary<string, object> _rootIndexIdentities = new(StringComparer.Ordinal);

    internal object RootIndexIdentity(string property) => _rootIndexIdentities.Count < MaximumPropertyPostingIndexes ||
        _rootIndexIdentities.ContainsKey(property) ? _rootIndexIdentities.GetOrAdd(property, static _ => new()) : this;

    internal void CarryUnchangedIndexes(GraphReadView before, IReadOnlyList<GraphCommittedEffect> effects)
    {
        if (ReferenceEquals(Edges, before.Edges))
        {
            _adjacency = before._adjacency;
            _completeCandidateAdjacency = before._completeCandidateAdjacency;
        }
        else if (before._adjacency is { } adjacency)
        {
            _adjacency = adjacency.Apply(before.Edges, Edges, effects);
        }
        // Caches describe immutable property/label columns. A name update must
        // not discard a number index, but inserts/deletes change every column.
        if (effects.Any(e => e is GraphVertexCreated or GraphVertexDeleted))
        {
            return;
        }

        var changed = effects.OfType<GraphVertexPropertyChanged>().Select(e => e.Property).ToHashSet(StringComparer.Ordinal);
        CarryNumericIndexes(before, changed);
        CarryNumericEqualityIndexes(before, changed);
        CarryStringEqualityIndexes(before, changed);
        CarryScalarNumericRangeIndexes(before, changed);
        // Ordered results contain immutable vertex identities, not old vertex
        // objects. Changes to another column cannot alter this ordering.
        if (before._orderedProperties is { } ordered)
        {
            foreach (var pair in ordered.Snapshot())
            {
                if (!changed.Contains(pair.Key.Property))
                {
                    OrderedPropertiesCache.Store(pair.Key, pair.Value);
                }
            }
        }

        if (before._propertyLookupCounts is { } counts)
        {
            foreach (var pair in counts)
            {
                if (!changed.Contains(pair.Key))
                {
                    RecordPropertyLookup(pair.Key, pair.Value);
                }
            }
        }

        if (before._propertyPostingIndexes is { } indexes)
        {
            foreach (var pair in indexes)
            {
                if (!changed.Contains(pair.Key) && pair.Value.IsValueCreated && pair.Value.Value.Postings is { } postings)
                {
                    PropertyPostingIndexesCache[pair.Key] = pair.Value;
                    _propertyPostingEntries += pair.Value.Value.EntryCount;
                }
            }
        }

        foreach (var pair in before._rootIndexIdentities)
        {
            if (!changed.Contains(pair.Key))
            {
                _rootIndexIdentities[pair.Key] = pair.Value;
            }
        }

        if (!effects.Any(e => e is GraphVertexLabelsChanged))
        {
            _labelPostings = before._labelPostings;
        }
    }

    private IReadOnlyDictionary<string, IReadOnlyList<GraphVertexId>>? _labelPostings;
    private GraphQueryCache<GraphLabeledRelationshipCountKey,
        GraphLabeledRelationshipCounts>? _labeledRelationshipCounts;

    private GraphQueryCache<GraphLabeledRelationshipCountKey,
        GraphLabeledRelationshipCounts> LabeledRelationshipCountsCache =>
        LazyInitializer.EnsureInitialized(ref _labeledRelationshipCounts, static () => new(value => value.Counts.Count));

    private GraphQueryCache<(GraphLabeledRelationshipCountKey Key,
        GraphPatternSortDirection Direction), IReadOnlyList<GraphLabeledRelationshipCount>>? _orderedLabeledRelationshipCounts;

    private GraphQueryCache<(GraphLabeledRelationshipCountKey Key,
        GraphPatternSortDirection Direction), IReadOnlyList<GraphLabeledRelationshipCount>> OrderedLabeledRelationshipCountsCache =>
        LazyInitializer.EnsureInitialized(ref _orderedLabeledRelationshipCounts, static () => new(value => value.Count));

    private GraphQueryCache<GraphTwoBranchCountKey, GraphTwoBranchCounts>? _twoBranchCounts;

    private GraphQueryCache<GraphTwoBranchCountKey, GraphTwoBranchCounts> TwoBranchCountsCache =>
        LazyInitializer.EnsureInitialized(ref _twoBranchCounts, static () => new(value => value.Counts.Count));

    private GraphQueryCache<(GraphTwoBranchCountKey Key, GraphPatternSortDirection Direction),
        IReadOnlyList<GraphTwoBranchCount>>? _orderedTwoBranchCounts;

    private GraphQueryCache<(GraphTwoBranchCountKey Key, GraphPatternSortDirection Direction),
        IReadOnlyList<GraphTwoBranchCount>> OrderedTwoBranchCountsCache =>
        LazyInitializer.EnsureInitialized(ref _orderedTwoBranchCounts, static () => new(value => value.Count));

    private GraphQueryCache<GraphTwoBranchCountKey, GraphTwoBranchTargetCounts>? _twoBranchTargetCounts;

    private GraphQueryCache<GraphTwoBranchCountKey, GraphTwoBranchTargetCounts> TwoBranchTargetCountsCache =>
        LazyInitializer.EnsureInitialized(ref _twoBranchTargetCounts, static () => new(value => value.Counts.Count));

    public static GraphReadView Empty
    {
        get;
    }
    = new(
        ImmutableDictionary<GraphVertexId, GraphVertex>.Empty,
        ImmutableDictionary<GraphEdgeId, GraphEdge>.Empty);

    public static GraphReadView Build(
        IReadOnlyDictionary<GraphVertexId, GraphVertex> vertices,
        IReadOnlyDictionary<GraphEdgeId, GraphEdge> edges) => new(
        vertices, edges);

    public GraphVertex? GetVertex(GraphVertexId id) =>
        Vertices.GetValueOrDefault(id);

    public GraphQueryStatistics StatisticsFor(DatabaseVersion version) =>
        LazyInitializer.EnsureInitialized(ref _statistics,
            () => GraphQueryStatistics.From(version, Vertices.Values, Edges.Values));

    public IReadOnlyList<GraphVertexId> FindByProperty(string property, DatabaseValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        if (PropertyPostingIndexesCache.TryGetValue(property, out var existingIndex))
        {
            IReadOnlyDictionary<DatabaseValue, IReadOnlyList<GraphVertexId>>? postings =
                existingIndex.Value.Postings;
            if (postings is not null)
            {
                return postings.GetValueOrDefault(value, []);
            }
        }
        if (PropertyValuesCache.TryGetValue((property, value), out IReadOnlyList<GraphVertexId>? cached))
        {
            return cached;
        }

        int lookups = RecordPropertyLookup(property);
        if (lookups >= PropertyPostingIndexAdmissionLookups &&
            (PropertyPostingIndexesCache.Count < MaximumPropertyPostingIndexes ||
             PropertyPostingIndexesCache.ContainsKey(property)))
        {
            Lazy<GraphPropertyPostingIndex> index =
                PropertyPostingIndexesCache.GetOrAdd(property, key => new(
                    () => BuildPropertyPostingIndex(key), LazyThreadSafetyMode.ExecutionAndPublication));
            IReadOnlyDictionary<DatabaseValue, IReadOnlyList<GraphVertexId>>? postings = index.Value.Postings;
            if (postings is not null)
            {
                return postings.GetValueOrDefault(value, []);
            }
        }

        if (PropertyValuesCache.Count >= MaximumPropertyValueCacheEntries)
        {
            PropertyValuesCache.Clear();
        }

        return PropertyValuesCache.GetOrAdd((property, value), key => Vertices.Values
            .Where(vertex => vertex.Properties.TryGetValue(key.Property, out DatabaseValue? candidate) &&
                candidate == key.Value)
            .Select(vertex => vertex.Id).ToArray());
    }

    private int RecordPropertyLookup(string property, int increment = 1)
    {
        lock (_propertyLookupGate)
        {
            var counts = PropertyLookupCountsCache;
            if (!counts.TryGetValue(property, out int count) && counts.Count >= MaximumPropertyValueCacheEntries)
            {
                counts.Clear();
            }

            int next = Math.Min(PropertyPostingIndexAdmissionLookups, count + increment);
            counts[property] = next;
            return next;
        }
    }

    private GraphPropertyPostingIndex BuildPropertyPostingIndex(
        string property)
    {
        var postings = new Dictionary<DatabaseValue, List<GraphVertexId>>();
        foreach (GraphVertex vertex in Vertices.Values)
        {
            if (!vertex.Properties.TryGetValue(property, out DatabaseValue? value))
            {
                continue;
            }

            if (!postings.TryGetValue(value, out List<GraphVertexId>? ids))
            {
                postings.Add(value, ids = []);
            }

            ids.Add(vertex.Id);
        }
        var immutable = postings.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<GraphVertexId>)pair.Value.ToArray());
        int entryCount = postings.Sum(pair => pair.Value.Count);
        lock (_propertyPostingGate)
        {
            if (entryCount > MaximumPropertyPostingEntries - _propertyPostingEntries)
            {
                return GraphPropertyPostingIndex.Unavailable;
            }

            _propertyPostingEntries += entryCount;
        }
        return new(immutable, entryCount);
    }

    internal void DeclareServingPropertyIndex(string property)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        if (PropertyPostingIndexesCache.Count >= MaximumPropertyPostingIndexes && !PropertyPostingIndexesCache.ContainsKey(property))
        {
            throw new InvalidOperationException("Declared serving indexes exceed the native index bound.");
        }

        var index = PropertyPostingIndexesCache.GetOrAdd(property, key => new(
            () => BuildPropertyPostingIndex(key), LazyThreadSafetyMode.ExecutionAndPublication));
        if (index.Value.Postings is null)
        {
            throw new InvalidOperationException("Declared serving index exceeds its posting bound.");
        }
    }

    public IReadOnlyList<GraphVertexId> FindByLabel(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return LazyInitializer.EnsureInitialized(ref _labelPostings, BuildLabelPostingIndex)
            .GetValueOrDefault(label, []);
    }

    private IReadOnlyDictionary<string, IReadOnlyList<GraphVertexId>> BuildLabelPostingIndex()
    {
        var postings = new Dictionary<string, List<GraphVertexId>>(StringComparer.Ordinal);
        foreach (GraphVertex vertex in Vertices.Values)
        {
            foreach (string label in vertex.Labels)
            {
                if (!postings.TryGetValue(label, out List<GraphVertexId>? ids))
                {
                    postings.Add(label, ids = []);
                }

                ids.Add(vertex.Id);
            }
        }

        return postings.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<GraphVertexId>)pair.Value.ToArray(), StringComparer.Ordinal);
    }

    public IReadOnlyList<GraphVertexId> OrderedByProperty(string property, GraphPatternSortDirection direction,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return OrderedPropertiesCache.GetOrAdd((property, direction), key =>
            {
                if (!Vertices.Values.Any(vertex => vertex.Properties.ContainsKey(key.Property)))
                {
                    return _identityOrder ??= Vertices.Keys.OrderBy(id => id.Value).ToArray();
                }

                return Vertices.Values.OrderBy(vertex => vertex, Comparer<GraphVertex>.Create((left, right) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool leftPresent = left.Properties.TryGetValue(key.Property, out DatabaseValue? leftValue);
                bool rightPresent = right.Properties.TryGetValue(key.Property, out DatabaseValue? rightValue);
                if (leftPresent != rightPresent)
                {
                    int presence = leftPresent ? -1 : 1;
                    return key.Direction == GraphPatternSortDirection.Ascending ? presence : -presence;
                }

                if (leftPresent)
                {
                    int comparison = GraphDatabaseValueComparer.Instance.Compare(leftValue, rightValue);
                    if (comparison != 0)
                    {
                        return key.Direction is GraphPatternSortDirection.Ascending
                        ? comparison : -comparison;
                    }
                }
                return left.Id.Value.CompareTo(right.Id.Value);
            }))
            .Select(vertex => vertex.Id).ToArray();
            });
        }
        catch (InvalidOperationException failure) when (failure.InnerException is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public IReadOnlyList<GraphEdge> Outgoing(GraphVertexId id, string? label) =>
        label is null ? Adjacency.Outgoing.GetValueOrDefault(id, []) :
        Adjacency.OutgoingByLabel.GetValueOrDefault(new(id, label), []);

    public IReadOnlyList<GraphEdge> Incoming(GraphVertexId id, string? label) =>
        label is null ? Adjacency.Incoming.GetValueOrDefault(id, []) :
        Adjacency.IncomingByLabel.GetValueOrDefault(new(id, label), []);

    public GraphLabeledRelationshipCounts LabeledRelationshipCounts(
        string edgeLabel, string sourceLabel, string targetLabel, GraphPatternDirection direction) =>
        LabeledRelationshipCountsCache.GetOrAdd(new(edgeLabel, sourceLabel, targetLabel, direction), key =>
        {
            var counts = new Dictionary<GraphVertexId, int>();
            int inspected = 0;
            foreach (GraphEdge edge in Edges.Values)
            {
                if (edge.Label != key.EdgeLabel)
                {
                    continue;
                }

                GraphVertexId sourceId = key.Direction is GraphPatternDirection.Outgoing ? edge.From : edge.To;
                GraphVertexId targetId = key.Direction is GraphPatternDirection.Outgoing ? edge.To : edge.From;
                GraphVertex? source = GetVertex(sourceId);
                GraphVertex? target = GetVertex(targetId);
                if (source is null || !source.Labels.Contains(key.SourceLabel))
                {
                    continue;
                }

                inspected++;
                if (target is null || !target.Labels.Contains(key.TargetLabel))
                {
                    continue;
                }

                counts[targetId] = counts.GetValueOrDefault(targetId) + 1;
            }
            return new(counts, inspected);
        });

    public IReadOnlyList<GraphLabeledRelationshipCount> OrderedLabeledRelationshipCounts(
        string edgeLabel, string sourceLabel, string targetLabel, GraphPatternDirection direction,
        GraphPatternSortDirection sortDirection)
    {
        var key = new GraphLabeledRelationshipCountKey(edgeLabel, sourceLabel, targetLabel, direction);
        return OrderedLabeledRelationshipCountsCache.GetOrAdd((key, sortDirection), item =>
        {
            GraphLabeledRelationshipCounts counts = LabeledRelationshipCounts(item.Key.EdgeLabel,
                item.Key.SourceLabel, item.Key.TargetLabel, item.Key.Direction);
            var ordered = counts.Counts.Select(entry => new GraphLabeledRelationshipCount(
                entry.Key, entry.Value, entry.Key.Value.ToString("D"),
                new LiteralValue(entry.Value.ToString(CultureInfo.InvariantCulture),
                    QueryScalarKind.Integer))).ToArray();
            Array.Sort(ordered, (left, right) =>
            {
                int comparison = left.Count.CompareTo(right.Count);
                if (item.Direction is GraphPatternSortDirection.Descending)
                {
                    comparison = -comparison;
                }

                return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Key, right.Key);
            });
            return ordered;
        });
    }

    public GraphTwoBranchCounts TwoBranchCounts(GraphTwoBranchCountKey key) =>
        TwoBranchCountsCache.GetOrAdd(key, item =>
        {
            var counts = new Dictionary<GraphVertexId, long>();
            int roots = 0, groupingEdges = 0, countedEdges = 0, matchedRows = 0;
            foreach (GraphVertex root in Vertices.Values)
            {
                if (!root.Labels.Contains(item.RootLabel))
                {
                    continue;
                }

                roots++;
                IReadOnlyList<GraphEdge> counted = item.CountedDirection is GraphPatternDirection.Outgoing
                    ? Outgoing(root.Id, item.CountedEdgeLabel) : Incoming(root.Id, item.CountedEdgeLabel);
                int count = 0;
                foreach (GraphEdge edge in counted)
                {
                    countedEdges++;
                    GraphVertexId targetId = item.CountedDirection is GraphPatternDirection.Outgoing
                        ? edge.To : edge.From;
                    if (GetVertex(targetId)?.Labels.Contains(item.CountedTargetLabel) == true)
                    {
                        count++;
                    }
                }
                if (count == 0)
                {
                    continue;
                }

                IReadOnlyList<GraphEdge> grouping = item.GroupingDirection is GraphPatternDirection.Outgoing
                    ? Outgoing(root.Id, item.GroupingEdgeLabel) : Incoming(root.Id, item.GroupingEdgeLabel);
                foreach (GraphEdge edge in grouping)
                {
                    groupingEdges++;
                    GraphVertexId targetId = item.GroupingDirection is GraphPatternDirection.Outgoing
                        ? edge.To : edge.From;
                    if (GetVertex(targetId)?.Labels.Contains(item.GroupingTargetLabel) != true)
                    {
                        continue;
                    }

                    counts[targetId] = counts.GetValueOrDefault(targetId) + count;
                    matchedRows = checked(matchedRows + count);
                }
            }
            return new(counts, roots, groupingEdges, countedEdges, matchedRows);
        });

    public IReadOnlyList<GraphTwoBranchCount> OrderedTwoBranchCounts(GraphTwoBranchCountKey key,
        GraphPatternSortDirection direction) =>
        OrderedTwoBranchCountsCache.GetOrAdd((key, direction), item =>
        {
            GraphTwoBranchCount[] ordered = TwoBranchCounts(item.Key).Counts
                .Select(entry => new GraphTwoBranchCount(entry.Key, entry.Value,
                    $"v:{entry.Key.Value:D}"))
                .ToArray();
            Array.Sort(ordered, (left, right) =>
            {
                int comparison = left.Count.CompareTo(right.Count);
                if (item.Direction is GraphPatternSortDirection.Descending)
                {
                    comparison = -comparison;
                }

                return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Key, right.Key);
            });
            return ordered;
        });

    public GraphTwoBranchTargetCounts TwoBranchTargetCounts(GraphTwoBranchCountKey key) =>
        TwoBranchTargetCountsCache.GetOrAdd(key, item =>
        {
            var counts = new Dictionary<GraphVertexId, int>();
            int roots = 0, groupingEdges = 0, countedEdges = 0, matchedRows = 0;
            foreach (GraphVertex root in Vertices.Values)
            {
                if (!root.Labels.Contains(item.RootLabel))
                {
                    continue;
                }

                roots++;
                int groupingMatches = 0;
                IReadOnlyList<GraphEdge> grouping = item.GroupingDirection is GraphPatternDirection.Outgoing
                    ? Outgoing(root.Id, item.GroupingEdgeLabel) : Incoming(root.Id, item.GroupingEdgeLabel);
                foreach (GraphEdge edge in grouping)
                {
                    groupingEdges++;
                    GraphVertexId targetId = item.GroupingDirection is GraphPatternDirection.Outgoing
                        ? edge.To : edge.From;
                    if (GetVertex(targetId)?.Labels.Contains(item.GroupingTargetLabel) == true)
                    {
                        groupingMatches++;
                    }
                }
                if (groupingMatches == 0)
                {
                    continue;
                }

                IReadOnlyList<GraphEdge> counted = item.CountedDirection is GraphPatternDirection.Outgoing
                    ? Outgoing(root.Id, item.CountedEdgeLabel) : Incoming(root.Id, item.CountedEdgeLabel);
                foreach (GraphEdge edge in counted)
                {
                    countedEdges++;
                    GraphVertexId targetId = item.CountedDirection is GraphPatternDirection.Outgoing
                        ? edge.To : edge.From;
                    if (GetVertex(targetId)?.Labels.Contains(item.CountedTargetLabel) != true)
                    {
                        continue;
                    }

                    counts[targetId] = checked(counts.GetValueOrDefault(targetId) + groupingMatches);
                    matchedRows = checked(matchedRows + groupingMatches);
                }
            }
            return new(counts, roots, groupingEdges, countedEdges, matchedRows);
        });

    private GraphAdjacencyIndexes Adjacency => LazyInitializer.EnsureInitialized(ref _adjacency,
        () => GraphAdjacencyIndexes.Build(Edges.Values));
}

internal sealed record GraphPropertyPostingIndex(
    IReadOnlyDictionary<DatabaseValue, IReadOnlyList<GraphVertexId>>? Postings, int EntryCount)
{
    public static GraphPropertyPostingIndex Unavailable
    {
        get;
    }
    =
        new((IReadOnlyDictionary<DatabaseValue, IReadOnlyList<GraphVertexId>>?)null, 0);
}

internal sealed record GraphQuerySnapshot(DatabaseVersion Version, GraphReadView View,
    GraphQueryStatistics Statistics);

internal readonly record struct GraphLabeledRelationshipCountKey(string EdgeLabel, string SourceLabel,
    string TargetLabel, GraphPatternDirection Direction);

internal sealed record GraphLabeledRelationshipCounts(IReadOnlyDictionary<GraphVertexId, int> Counts,
    int InspectedEdges);

internal readonly record struct GraphLabeledRelationshipCount(
    GraphVertexId Target, int Count, string Key, LiteralValue Value);

internal readonly record struct GraphTwoBranchCountKey(string RootLabel,
    string GroupingEdgeLabel, GraphPatternDirection GroupingDirection, string GroupingTargetLabel,
    string CountedEdgeLabel, GraphPatternDirection CountedDirection, string CountedTargetLabel);

internal sealed record GraphTwoBranchCounts(IReadOnlyDictionary<GraphVertexId, long> Counts, int Roots,
    int GroupingEdges, int CountedEdges, int MatchedRows);

internal readonly record struct GraphTwoBranchCount(GraphVertexId Target, long Count, string Key);

internal sealed record GraphTwoBranchTargetCounts(IReadOnlyDictionary<GraphVertexId, int> Counts, int Roots,
    int GroupingEdges, int CountedEdges, int MatchedRows);

internal sealed record GraphAdjacencyIndexes(
    IReadOnlyDictionary<GraphVertexId, IReadOnlyList<GraphEdge>> Outgoing,
    IReadOnlyDictionary<GraphVertexId, IReadOnlyList<GraphEdge>> Incoming,
    IReadOnlyDictionary<GraphAdjacencyKey, IReadOnlyList<GraphEdge>> OutgoingByLabel,
    IReadOnlyDictionary<GraphAdjacencyKey, IReadOnlyList<GraphEdge>> IncomingByLabel)
{
    internal GraphAdjacencyIndexes Apply(IReadOnlyDictionary<GraphEdgeId, GraphEdge> before,
        IReadOnlyDictionary<GraphEdgeId, GraphEdge> after, IReadOnlyList<GraphCommittedEffect> effects)
    {
        var outgoing = Outgoing.ToImmutableDictionary();
        var incoming = Incoming.ToImmutableDictionary();
        var outgoingLabels = OutgoingByLabel.ToImmutableDictionary();
        var incomingLabels = IncomingByLabel.ToImmutableDictionary();
        var ids = effects.Select(effect => effect switch
        {
            GraphEdgeCreated created => (GraphEdgeId?)created.Edge.Id,
            GraphEdgeDeleted deleted => deleted.Edge.Id,
            GraphEdgePropertyChanged changed => changed.Edge,
            _ => null
        }).OfType<GraphEdgeId>().Distinct();
        foreach (var id in ids)
        {
            if (before.TryGetValue(id, out var old))
            {
                Change(old, false);
            }

            if (after.TryGetValue(id, out var next))
            {
                Change(next, true);
            }
        }
        return new(outgoing, incoming, outgoingLabels, incomingLabels);

        void Change(GraphEdge edge, bool add)
        {
            if (edge.StorageRole.HasFlag(GraphEdgeStorageRole.OutgoingAdjacency))
            {
                outgoing = Update(outgoing, edge.From, edge, add);
                outgoingLabels = Update(outgoingLabels, new(edge.From, edge.Label), edge, add);
            }
            if (edge.StorageRole.HasFlag(GraphEdgeStorageRole.IncomingAdjacency))
            {
                incoming = Update(incoming, edge.To, edge, add);
                incomingLabels = Update(incomingLabels, new(edge.To, edge.Label), edge, add);
            }
        }
        static ImmutableDictionary<TKey, IReadOnlyList<GraphEdge>> Update<TKey>(
            ImmutableDictionary<TKey, IReadOnlyList<GraphEdge>> map, TKey key, GraphEdge edge, bool add) where TKey : notnull
        {
            var values = map.GetValueOrDefault(key, Array.Empty<GraphEdge>()).Where(item => item.Id != edge.Id);
            var result = (add ? values.Append(edge) : values).ToArray();
            return result.Length == 0 ? map.Remove(key) : map.SetItem(key, Array.AsReadOnly(result));
        }
    }

    public static GraphAdjacencyIndexes Build(IEnumerable<GraphEdge> edges)
    {
        var outgoing = new Dictionary<GraphVertexId, List<GraphEdge>>();
        var incoming = new Dictionary<GraphVertexId, List<GraphEdge>>();
        var outgoingByLabel = new Dictionary<GraphAdjacencyKey, List<GraphEdge>>();
        var incomingByLabel = new Dictionary<GraphAdjacencyKey, List<GraphEdge>>();
        foreach (GraphEdge edge in edges)
        {
            if (edge.StorageRole.HasFlag(GraphEdgeStorageRole.OutgoingAdjacency))
            {
                if (!outgoing.TryGetValue(edge.From, out List<GraphEdge>? from))
                {
                    outgoing[edge.From] = from = [];
                }

                from.Add(edge);
                GraphAdjacencyKey outgoingKey = new(edge.From, edge.Label);
                if (!outgoingByLabel.TryGetValue(outgoingKey, out List<GraphEdge>? labeledFrom))
                {
                    outgoingByLabel[outgoingKey] = labeledFrom = [];
                }

                labeledFrom.Add(edge);
            }
            if (edge.StorageRole.HasFlag(GraphEdgeStorageRole.IncomingAdjacency))
            {
                if (!incoming.TryGetValue(edge.To, out List<GraphEdge>? to))
                {
                    incoming[edge.To] = to = [];
                }

                to.Add(edge);
                GraphAdjacencyKey incomingKey = new(edge.To, edge.Label);
                if (!incomingByLabel.TryGetValue(incomingKey, out List<GraphEdge>? labeledTo))
                {
                    incomingByLabel[incomingKey] = labeledTo = [];
                }

                labeledTo.Add(edge);
            }
        }
        // Historical public reads share these lists; keep the cache-owned arrays inaccessible.
        return new(outgoing.ToImmutableDictionary(pair => pair.Key,
                pair => (IReadOnlyList<GraphEdge>)Array.AsReadOnly(pair.Value.ToArray())),
            incoming.ToImmutableDictionary(pair => pair.Key,
                pair => (IReadOnlyList<GraphEdge>)Array.AsReadOnly(pair.Value.ToArray())),
            outgoingByLabel.ToImmutableDictionary(pair => pair.Key,
                pair => (IReadOnlyList<GraphEdge>)Array.AsReadOnly(pair.Value.ToArray())),
            incomingByLabel.ToImmutableDictionary(pair => pair.Key,
                pair => (IReadOnlyList<GraphEdge>)Array.AsReadOnly(pair.Value.ToArray())));
    }
}

internal sealed class GraphDatabaseValueComparer : IComparer<DatabaseValue>
{
    public static GraphDatabaseValueComparer Instance { get; } = new();

    public int Compare(DatabaseValue? left, DatabaseValue? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        if (left.GetType() != right.GetType())
        {
            return left.Kind.CompareTo(right.Kind);
        }

        return (left, right) switch
        {
            (DatabaseValue.String first, DatabaseValue.String second) =>
                EntityForge.Db.Query.Planning.UnicodeText.CompareCodepoints(first.Value, second.Value),
            (DatabaseValue.Date first, DatabaseValue.Date second) => first.Value.CompareTo(second.Value),
            (DatabaseValue.Duration first, DatabaseValue.Duration second) => first.Qualifier != second.Qualifier ? first.Qualifier.CompareTo(second.Qualifier) : first.Value.CompareTo(second.Value),
            (DatabaseValue.LocalDateTime first, DatabaseValue.LocalDateTime second) => first.Value.CompareTo(second.Value),
            (DatabaseValue.LocalTime first, DatabaseValue.LocalTime second) => first.Value.CompareTo(second.Value),
            (DatabaseValue.Uuid first, DatabaseValue.Uuid second) => first.Value.CompareTo(second.Value),
            (DatabaseValue.Decimal first, DatabaseValue.Decimal second) => first.Value.CompareTo(second.Value),
            (DatabaseValue.ZonedTime first, DatabaseValue.ZonedTime second) => first.Value.CompareTo(second.Value),
            (DatabaseValue.ZonedDateTime first, DatabaseValue.ZonedDateTime second) => first.Value.CompareTo(second.Value),
            (DatabaseValue.Point first, DatabaseValue.Point second) => first.Value.CompareTo(second.Value),
            (DatabaseValue.Int64 first, DatabaseValue.Int64 second) => first.Value.CompareTo(second.Value),
            (DatabaseValue.Double first, DatabaseValue.Double second) => first.Value.CompareTo(second.Value),
            (DatabaseValue.Boolean first, DatabaseValue.Boolean second) => first.Value.CompareTo(second.Value),
            (DatabaseValue.DateTimeOffset first, DatabaseValue.DateTimeOffset second) =>
                first.Value.CompareTo(second.Value),
            (DatabaseValue.Uri first, DatabaseValue.Uri second) =>
                StringComparer.Ordinal.Compare(first.Value.AbsoluteUri, second.Value.AbsoluteUri),
            _ => throw new NotSupportedException($"Graph ordering does not support '{left.Kind}' values.")
        };
    }
}
