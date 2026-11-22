using System.Globalization;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Query.Planning;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Graph;

internal sealed partial class BoundedGraphPatternExecutor(
    GraphProfileRuntime? runtime,
    Func<ValueTask>? afterSnapshotCaptured = null,
    GraphReadView? capturedView = null)
{
    private static readonly Uri Provider = new("urn:entityforge:database:graph");
    private static readonly ConcurrentDictionary<(string Kind, string Value, SemanticTermRole Role), SemanticTerm>
        Terms = new();

    private static readonly ConditionalWeakTable<QueryValue, DatabaseValueConversion> DatabaseValues = new();
    private const int MinimumRootsForAggregateEdgeScan = 128;
    private const int MaximumSemanticTermCacheEntries = 4_096;
    private const int MaximumMaterializedNodeCacheEntries = 16_384;
    private ConcurrentDictionary<(long Version, GraphVertexId Vertex), MaterializedNode>
        _materializedNodes = new();

    public async ValueTask<QueryResultEnvelope> ExecuteAsync(BoundedGraphPatternQuery query,
        DatabaseVersion snapshot, CancellationToken cancellationToken)
        => (await ExecuteAnalyzedAsync(query, snapshot, cancellationToken).ConfigureAwait(false)).Result;

    internal async ValueTask<QueryResultEnvelope> ExecuteCapturedResultAsync(BoundedGraphPatternQuery query,
        GraphQuerySnapshot snapshot, CancellationToken cancellationToken,
        Func<Func<string, string, QueryValue>, GraphPatternMatch>? projection = null, bool preserveGraph = true,
        IReadOnlyList<GraphVertexId>? startCandidates = null) =>
        (await ExecuteCapturedAsync(query, snapshot, cancellationToken, projection, preserveGraph, startCandidates).ConfigureAwait(false)).Result;

    internal GraphCandidatePlanDescription DescribeCandidates(BoundedGraphPatternQuery query,
        GraphQuerySnapshot snapshot)
    {
        GraphCandidateQuery candidates = CandidateQuery(query);
        if (TryAnchoredCandidates(query, snapshot.View, candidates, out var anchored))
        {
            return new(snapshot.Statistics.Version, ["snapshot.graph-selective-target-adjacency"], [anchored.Count],
                [new QueryEstimate(anchored.Count, QueryEstimateQuality.UpperBound, "snapshot.graph-read-view")]);
        }
        if (candidates.ScalarStringEquality is { } text &&
            snapshot.View.FindScalarStringEquality(text) is { } textCandidates)
        {
            return new(snapshot.Statistics.Version, ["snapshot.graph-string-equality"], [textCandidates.Count],
                [new QueryEstimate(textCandidates.Count, QueryEstimateQuality.UpperBound, "snapshot.graph-read-view")]);
        }
        if (candidates.ScalarNumericEquality is { } equality &&
            snapshot.View.FindScalarNumericEquality(equality) is { } equalCandidates)
        {
            return new(snapshot.Statistics.Version, ["snapshot.graph-numeric-equality"], [equalCandidates.Count],
                [new QueryEstimate(equalCandidates.Count, QueryEstimateQuality.UpperBound, "snapshot.graph-read-view")]);
        }
        if (candidates.ScalarNumericBounds is { Count: > 0 } scalarBounds &&
            snapshot.View.FindScalarNumericRange(scalarBounds) is { } scalarRange)
        {
            return new(snapshot.Statistics.Version, ["snapshot.graph-scalar-numeric-range"], [scalarRange.Count],
                [new QueryEstimate(scalarRange.Count, QueryEstimateQuality.UpperBound, "snapshot.graph-read-view")]);
        }
        if (candidates.NumericBounds is { Count: > 0 } bounds &&
            snapshot.View.FindNumericCandidates(bounds) is { } numeric)
        {
            return new(snapshot.Statistics.Version, ["snapshot.graph-numeric-range"], [numeric.Count],
                [new QueryEstimate(numeric.Count, QueryEstimateQuality.Exact, "snapshot.graph-read-view")]);
        }

        string accessPath = candidates.Property is null
            ? "snapshot.graph-vertex-scan" : "snapshot.graph-vertex-property-index";
        long count = SnapshotCandidates(snapshot.View, candidates).Count;
        return new(snapshot.Statistics.Version, [accessPath], [count],
            [new QueryEstimate(count, QueryEstimateQuality.Exact, "snapshot.graph-read-view")]);
    }

    internal IReadOnlyList<GraphRelationshipPlanEntry> PlanRelationships(BoundedGraphPatternQuery query,
        GraphQuerySnapshot snapshot) => PlanRelationships(query, snapshot.Statistics);

    private static IReadOnlyList<GraphRelationshipPlanEntry> PlanRelationships(
        BoundedGraphPatternQuery query, GraphQueryStatistics statistics)
    {
        GraphRelationshipPlanEntry[] plan = query.Relationships.Select((pattern, index) =>
            new GraphRelationshipPlanEntry(index, pattern, null, false)).ToArray();
        HashSet<string> introduced = [query.Start.Binding];
        var safe = new bool[plan.Length];
        for (int index = 0; index < plan.Length; index++)
        {
            GraphPatternRelationship relationship = plan[index].Pattern;
            bool introduces = introduced.Add(relationship.Target.Binding);
            bool targetUsedLater = query.Relationships.Skip(index + 1).Any(item =>
                item.SourceBinding == relationship.Target.Binding || item.Target.Binding == relationship.Target.Binding);
            safe[index] = introduces && !targetUsedLater &&
                relationship.MatchKind is GraphPatternMatchKind.Required && relationship.Hops.IsFixed &&
                relationship.PathSelection is GraphPatternPathSelection.All;
        }
        int start = 0;
        while (start < plan.Length)
        {
            if (!safe[start])
            {
                start++;
                continue;
            }
            string source = plan[start].Pattern.SourceBinding;
            int end = start + 1;
            while (end < plan.Length && safe[end] && plan[end].Pattern.SourceBinding == source)
            {
                end++;
            }

            GraphRelationshipPlanEntry[] ordered = plan[start..end].Select(item => item with
            {
                EstimatedCost = RelationshipCost(item.Pattern, statistics),
                Reorderable = end - start > 1
            })
                .OrderBy(item => item.EstimatedCost).ThenBy(item => item.OriginalIndex).ToArray();
            Array.Copy(ordered, 0, plan, start, ordered.Length);
            start = end;
        }
        return plan;
    }

    private static double RelationshipCost(GraphPatternRelationship relationship,
        GraphQueryStatistics statistics)
    {
        double selectivity = 1D;
        if (relationship.Target.Labels.Count > 0 && statistics.VertexCount > 0)
        {
            selectivity = Math.Min(selectivity, relationship.Target.Labels.Min(label =>
                statistics.VertexLabelBound(label)) / (double)statistics.VertexCount);
        }

        if (statistics.VertexCount > 0)
        {
            foreach (GraphPatternPropertyPredicate predicate in relationship.Target.Predicates.Where(item =>
                item.Operator is GraphPatternComparisonOperator.Equal && TryDatabaseValue(item.Value, out _)))
            {
                DatabaseValue value = DatabaseValue(predicate.Value);
                long estimate = statistics.EstimateVertexValue(predicate.Property, value) ??
                    statistics.VertexPropertyBound(predicate.Property);
                selectivity = Math.Min(selectivity, estimate / (double)statistics.VertexCount);
            }
        }

        return statistics.AverageDegree(relationship.Label) * selectivity;
    }

    public async ValueTask<BoundedGraphPatternExecution> ExecuteAnalyzedAsync(BoundedGraphPatternQuery query,
        DatabaseVersion snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        using GraphSnapshotLease queryLease = (runtime ?? throw new InvalidOperationException("A native runtime is required.")).AcquireSnapshot(snapshot);
        GraphQuerySnapshot querySnapshot = runtime!.CaptureQuerySnapshot(snapshot);
        return await ExecuteCapturedAsync(query, querySnapshot, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<BoundedGraphPatternExecution> ExecuteCapturedAsync(BoundedGraphPatternQuery query,
        GraphQuerySnapshot querySnapshot, CancellationToken cancellationToken,
        Func<Func<string, string, QueryValue>, GraphPatternMatch>? projection = null, bool preserveGraph = true,
        IReadOnlyList<GraphVertexId>? startCandidates = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        // Every optimized and general operator must use this exact immutable view.
        // A per-execution worker prevents concurrent snapshots from sharing mutable context.
        if (capturedView is null)
        {
            return await new BoundedGraphPatternExecutor(runtime, afterSnapshotCaptured, querySnapshot.View)
            {
                _materializedNodes = _materializedNodes
            }.ExecuteCapturedAsync(query, querySnapshot, cancellationToken, projection, preserveGraph, startCandidates).ConfigureAwait(false);
        }

        DatabaseVersion snapshot = querySnapshot.Version;
        GraphCandidateQuery candidates = CandidateQuery(query);
        if (afterSnapshotCaptured is not null)
        {
            await afterSnapshotCaptured().ConfigureAwait(false);
        }

        if (query.MaximumMatches == 0 || query.Aggregation is { Limit: 0 })
        {
            return query.Aggregation is { } emptyAggregate
                ? Aggregate(emptyAggregate, [], snapshot, 0, TimeSpan.Zero, [], 0, 0, cancellationToken)
                : new(MaterializeMatches(query, [], snapshot, projection, preserveGraph), 0, TimeSpan.Zero, [], 0, 0, null, TimeSpan.Zero);
        }

        GraphReadView executionView = querySnapshot.View;
        long candidateStarted = Stopwatch.GetTimestamp();
        IReadOnlyList<GraphVertexId> roots = [];
        bool candidatesOrdered = startCandidates is null && TryOrderedRootCandidates(query, executionView, cancellationToken, out roots);
        if (startCandidates is not null)
        {
            roots = startCandidates.Where(executionView.Vertices.ContainsKey).Distinct().ToArray();
        }
        else if (!candidatesOrdered)
        {
            roots = TryAnchoredCandidates(query, executionView, candidates, out var anchored, cancellationToken)
                ? anchored : SnapshotCandidates(executionView, candidates, cancellationToken);
        }

        TimeSpan candidateDuration = Stopwatch.GetElapsedTime(candidateStarted);

        if (TryExecuteGroupedRootScalars(query, executionView, snapshot, roots, candidateDuration,
                cancellationToken, out var groupedRoot))
        {
            return groupedRoot!;
        }

        // Preserve this precedence: specialised operators must accept the exact captured view
        // and return false for unsupported shapes before general traversal takes over.
        bool directed = query.Relationships.All(relationship => relationship.EdgePredicates.Count == 0 && !relationship.AllowRepeatedRelationships && relationship.PathMode == GraphPatternPathMode.Walk) &&
            (query.Aggregation is null || SingleAggregation(query.Aggregation) is not null) && SupportsIndependentDirectedSteps(query);
        if (directed && TryExecuteRootAggregate(query, snapshot, roots, candidateDuration,
                cancellationToken, out BoundedGraphPatternExecution? rootAggregateExecution))
        {
            return rootAggregateExecution!;
        }

        if (directed && TryExecuteTwoBranchCount(query, snapshot, roots, candidateDuration,
                cancellationToken, out BoundedGraphPatternExecution? branchedAggregateExecution))
        {
            return branchedAggregateExecution!;
        }

        if (directed && TryExecuteFixedLinearCount(query, snapshot, querySnapshot.Statistics, roots, candidateDuration,
                cancellationToken, out BoundedGraphPatternExecution? linearAggregateExecution))
        {
            return linearAggregateExecution!;
        }

        if (directed && TryExecuteFixedOneHopCount(query, snapshot, roots, candidateDuration,
                cancellationToken, out BoundedGraphPatternExecution? aggregateExecution))
        {
            return aggregateExecution!;
        }

        if (directed && projection is null && query.OrderBy.All(term => term.Expression is null) && TryExecuteRootOnly(query, snapshot, roots, candidateDuration, candidatesOrdered,
                out BoundedGraphPatternExecution? rootExecution))
        {
            return rootExecution!;
        }

        if (directed && projection is null && query.OrderBy.All(term => term.Expression is null) && TryExecuteFixedOneHopProjection(query, snapshot, roots, candidateDuration,
                cancellationToken, out BoundedGraphPatternExecution? oneHopProjectionExecution))
        {
            return oneHopProjectionExecution!;
        }

        if (directed && projection is null && query.OrderBy.All(term => term.Expression is null) && TryExecuteFixedLinearProjection(query, snapshot, querySnapshot.Statistics, roots, candidateDuration,
                cancellationToken, out BoundedGraphPatternExecution? projectionExecution))
        {
            return projectionExecution!;
        }

        if (directed && projection is null && query.OrderBy.All(term => term.Expression is null) && TryExecuteTwoBranchOrdered(query, snapshot, roots, candidateDuration,
                out BoundedGraphPatternExecution? twoBranchOrderedExecution))
        {
            return twoBranchOrderedExecution!;
        }

        if (directed && projection is null && query.OrderBy.All(term => term.Expression is null) && TryExecuteFixedLinearOrdered(query, snapshot, querySnapshot.Statistics, roots, candidateDuration,
                cancellationToken, out BoundedGraphPatternExecution? orderedExecution))
        {
            return orderedExecution!;
        }

        var states = roots.Select(id => executionView.GetVertex(id))
            .OfType<GraphVertex>().Where(item => Matches(item, query.Start))
            .Select(item => MatchState.Start(query.Start.Binding, item)).ToList();
        int expansionCount = states.Count;
        long materializedPathElements = 0;
        void ChargePath(int elements)
        {
            materializedPathElements += elements;
            if (materializedPathElements > GraphPatternLimits.MaximumMaterializedPathElements)
            {
                throw new ArgumentException("Graph path enumeration exceeds its materialized-element work bound.");
            }
        }
        EnsureWithinExpansionLimit(expansionCount, query.MaximumExpansions);
        IReadOnlyList<GraphRelationshipPlanEntry> relationshipPlan = PlanRelationships(
            query, querySnapshot.Statistics);
        var relationshipAnalyses = new List<GraphPatternRelationshipExecution>(query.Relationships.Count);
        var boundNodeBindings = new HashSet<string>(StringComparer.Ordinal) { query.Start.Binding };
        for (int relationshipIndex = 0; relationshipIndex < relationshipPlan.Count; relationshipIndex++)
        {
            GraphRelationshipPlanEntry planned = relationshipPlan[relationshipIndex];
            GraphPatternRelationship relationship = planned.Pattern;
            bool introducesTarget = boundNodeBindings.Add(relationship.Target.Binding);
            int inputStates = states.Count;
            int inspectedEdges = 0;
            int preservedRows = 0;
            int enumeratedPaths = 0;
            int discardedPaths = 0;
            long relationshipStarted = Stopwatch.GetTimestamp();
            var next = new List<MatchState>();
            foreach (MatchState state in states)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GraphVertex? source = state.VertexOrDefault(relationship.SourceBinding);
                GraphVertex? requiredTarget = introducesTarget
                    ? null : state.VertexOrDefault(relationship.Target.Binding);
                if (source is null || relationship.PathSelection != GraphPatternPathSelection.All && relationship.PathSelectionCount == 0)
                {
                    if (relationship.MatchKind is GraphPatternMatchKind.Optional)
                    {
                        next.Add(state.AppendUnbound(relationship, introducesTarget));
                        preservedRows++;
                    }
                    continue;
                }
                bool matched = false;
                var candidatesForState = new List<PathTraversal>();
                var frontier = new List<PathTraversal> { new(source, [], [source]) };
                // Below the minimum length, cycles can be necessary. Once the minimum
                // is reached, only the first k distinct arrival depths can contribute
                // to the first k length groups (and hence the first k paths) at any
                // endpoint. Equal-depth arrivals preserve path multiplicity.
                Dictionary<GraphVertexId, (int Depth, int Groups)>? shortestDepths =
                    relationship.Hops.Maximum is null && relationship.AllowRepeatedRelationships &&
                    relationship.PathMode == GraphPatternPathMode.Walk &&
                    relationship.PathSelection != GraphPatternPathSelection.All ? new() : null;
                if (shortestDepths is not null && relationship.Hops.Minimum == 0)
                {
                    shortestDepths[source.Id] = (0, 1);
                }
                ChargePath(1);
                if (relationship.Hops.Minimum == 0 && (requiredTarget is null || source.Id == requiredTarget.Id) &&
                    Matches(source, relationship.Target))
                {
                    candidatesForState.Add(frontier[0]);
                }
                for (int depth = 1; depth <= (relationship.Hops.Maximum ?? Int32.MaxValue) && frontier.Count > 0; depth++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var following = new List<PathTraversal>();
                    foreach (PathTraversal traversal in frontier)
                    {
                        if (relationship.PathMode == GraphPatternPathMode.Simple && traversal.Edges.Count > 0 && traversal.Current.Id == source.Id)
                        {
                            continue;
                        }
                        IEnumerable<GraphEdge> edges = relationship.Direction switch
                        {
                            GraphPatternDirection.Outgoing => executionView.Outgoing(traversal.Current.Id, null),
                            GraphPatternDirection.Incoming => executionView.Incoming(traversal.Current.Id, null),
                            GraphPatternDirection.Undirected => executionView.Outgoing(traversal.Current.Id, null)
                                .Concat(executionView.Incoming(traversal.Current.Id, null)).DistinctBy(edge => edge.Id),
                            _ => throw new ArgumentOutOfRangeException(nameof(relationship.Direction))
                        };
                        foreach (GraphEdge edge in edges)
                        {
                            if (relationship.Label is not null && edge.Label != relationship.Label)
                            {
                                continue;
                            }

                            inspectedEdges++;
                            expansionCount++;
                            EnsureWithinExpansionLimit(expansionCount, query.MaximumExpansions);
                            if (!MatchesEdgeProperties(edge, relationship.EdgePredicates))
                            {
                                continue;
                            }
                            if (!relationship.AllowRepeatedRelationships && (traversal.Edges.Any(item => item.Id == edge.Id) ||
                                relationship.MatchKind == GraphPatternMatchKind.Required && state.Relationships.Any(prior =>
                                    prior.Pattern.MatchKind == GraphPatternMatchKind.Required && prior.Edges.Any(item => item.Id == edge.Id))))
                            {
                                continue;
                            }

                            GraphVertexId targetId = relationship.Direction is GraphPatternDirection.Outgoing ||
                                relationship.Direction is GraphPatternDirection.Undirected && edge.From == traversal.Current.Id
                                ? edge.To : edge.From;
                            if (relationship.PathMode == GraphPatternPathMode.Trail && traversal.Edges.Any(item => item.Id == edge.Id) ||
                                relationship.PathMode == GraphPatternPathMode.Acyclic && traversal.Vertices.Any(item => item.Id == targetId) ||
                                relationship.PathMode == GraphPatternPathMode.Simple && targetId != source.Id && traversal.Vertices.Any(item => item.Id == targetId))
                            {
                                continue;
                            }
                            GraphVertex? target = executionView.GetVertex(targetId);
                            if (target is null)
                            {
                                continue;
                            }

                            if (shortestDepths is not null && depth >= relationship.Hops.Minimum)
                            {
                                shortestDepths.TryGetValue(targetId, out var earlier);
                                bool newDepth = earlier.Groups == 0 || earlier.Depth < depth;
                                if (newDepth && earlier.Groups >= relationship.PathSelectionCount)
                                {
                                    continue;
                                }
                                shortestDepths[targetId] = (depth, earlier.Groups + (newDepth ? 1 : 0));
                            }
                            ChargePath(traversal.Edges.Count * 2 + 3);
                            var candidate = new PathTraversal(target, [.. traversal.Edges, edge],
                                [.. traversal.Vertices, target]);
                            following.Add(candidate);
                            if (depth < relationship.Hops.Minimum ||
                                (requiredTarget is not null && target.Id != requiredTarget.Id) ||
                                !Matches(target, relationship.Target))
                            {
                                continue;
                            }

                            candidatesForState.Add(candidate);
                        }
                    }
                    frontier = following;
                }
                enumeratedPaths += candidatesForState.Count;
                IReadOnlyList<PathTraversal> selected = Select(candidatesForState, relationship.PathSelection, relationship.PathSelectionCount);
                if (relationship.Filter is { } optionalFilter)
                {
                    selected = selected.Where(candidate => Matches(
                        state.Append(relationship, candidate.Edges, candidate.Vertices, introducesTarget), optionalFilter)).ToArray();
                }
                discardedPaths += candidatesForState.Count - selected.Count;
                foreach (PathTraversal candidate in selected)
                {
                    next.Add(state.Append(relationship, candidate.Edges, candidate.Vertices, introducesTarget));
                }

                matched = selected.Count > 0;
                if (!matched && relationship.MatchKind is GraphPatternMatchKind.Optional)
                {
                    next.Add(state.AppendUnbound(relationship, introducesTarget));
                    preservedRows++;
                }
            }
            states = next;
            relationshipAnalyses.Add(new(planned.OriginalIndex, inputStates, inspectedEdges, enumeratedPaths, discardedPaths,
                states.Count, preservedRows,
                Stopwatch.GetElapsedTime(relationshipStarted)));
            if (states.Count == 0)
            {
                break;
            }
        }

        if (query.Filter is not null)
        {
            states = states.Where(state => Matches(state, query.Filter)).ToList();
        }

        int rowsBeforeLimit = states.Count;
        if (query.Aggregation is not null)
        {
            return Aggregate(query.Aggregation, states, snapshot, roots.Count, candidateDuration,
                relationshipAnalyses, expansionCount, rowsBeforeLimit, cancellationToken);
        }

        TimeSpan? sortDuration = null;
        if (query.OrderBy.Count > 0)
        {
            long sortStarted = Stopwatch.GetTimestamp();
            QueryComparisonSort.Sort(states, (left, right) => Compare(left, right, query), cancellationToken);
            sortDuration = Stopwatch.GetElapsedTime(sortStarted);
        }
        if (query.Distinct)
        {
            states = projection is null ? states.DistinctBy(state => ProjectionKey(state, query)).ToList()
                : states.DistinctBy(state => String.Concat(projection(state.PropertyValue).Bindings.Values.Select(value =>
                {
                    string key = GraphScalarEvaluator.DistinctKey(value);
                    return key.Length.ToString(CultureInfo.InvariantCulture) + ":" + key;
                }))).ToList();
        }

        states = states.Skip(query.Offset).Take(query.MaximumMatches).ToList();

        long projectionStarted = Stopwatch.GetTimestamp();
        var result = MaterializeMatches(query, states, snapshot, projection, preserveGraph);
        return new(result, roots.Count, candidateDuration, relationshipAnalyses, expansionCount,
            rowsBeforeLimit, sortDuration, Stopwatch.GetElapsedTime(projectionStarted));
    }

    private QueryResultEnvelope MaterializeMatches(BoundedGraphPatternQuery query,
        IReadOnlyList<MatchState> states, DatabaseVersion snapshot,
        Func<Func<string, string, QueryValue>, GraphPatternMatch>? projection = null, bool preserveGraph = true)
    {
        var nodes = new Dictionary<GraphVertexId, NodeValue>();
        var edgesById = new Dictionary<GraphEdgeId, EdgeValue>();
        var evidence = new Dictionary<EvidenceReference, EvidenceRecord>();
        var matches = new List<GraphPatternMatch>(states.Count);
        HashSet<string> returned = query.ReturnedBindings.ToHashSet(StringComparer.Ordinal);
        foreach (MatchState state in states)
        {
            if (projection is not null && !preserveGraph)
            {
                matches.Add(projection(state.PropertyValue));
                continue;
            }
            var bindings = new List<KeyValuePair<string, QueryValue>>();
            var requiredNodes = returned.Where(binding => state.VertexOrDefault(binding) is not null)
                .ToHashSet(StringComparer.Ordinal);
            foreach (MatchedRelationship relationship in state.Relationships.Where(item =>
                returned.Contains(item.Pattern.Binding) && item.Vertices.Count > 0))
            {
                requiredNodes.Add(relationship.Pattern.SourceBinding);
                requiredNodes.Add(relationship.Pattern.Target.Binding);
            }
            var stateNodes = projection is null ? new Dictionary<string, NodeValue>(StringComparer.Ordinal) : null;
            foreach (string binding in requiredNodes)
            {
                GraphVertex vertex = state.Vertex(binding);
                NodeValue node = nodes.GetValueOrDefault(vertex.Id) ?? Node(vertex, snapshot, evidence);
                nodes[vertex.Id] = node;
                stateNodes?.Add(binding, node);
            }
            if (projection is not null)
            {
                // Keep the rich graph and evidence, but construct scalar matches
                // directly from the owned native facts rather than decoding NodeValue.
                foreach (MatchedRelationship relationship in state.Relationships.Where(item => returned.Contains(item.Pattern.Binding)))
                {
                    foreach (GraphEdge edge in relationship.Edges)
                    {
                        edgesById[edge.Id] = edgesById.GetValueOrDefault(edge.Id) ?? Edge(edge, snapshot, evidence);
                    }
                }
                matches.Add(projection(state.PropertyValue));
                continue;
            }
            var paths = new List<PathValue>(state.Relationships.Count);
            var stateRelationships = new Dictionary<string, QueryValue>(StringComparer.Ordinal);
            foreach (MatchedRelationship relationship in state.Relationships.Where(item =>
                returned.Contains(item.Pattern.Binding) && item.Vertices.Count > 0))
            {
                var elements = new List<QueryValue>(relationship.Edges.Count * 2 + 1);
                for (int index = 0; index < relationship.Vertices.Count; index++)
                {
                    GraphVertex vertex = relationship.Vertices[index];
                    NodeValue node = nodes.GetValueOrDefault(vertex.Id) ?? Node(vertex, snapshot, evidence);
                    nodes[vertex.Id] = node;
                    elements.Add(node);
                    if (index == relationship.Edges.Count)
                    {
                        continue;
                    }

                    GraphEdge edge = relationship.Edges[index];
                    EdgeValue edgeValue = edgesById.GetValueOrDefault(edge.Id) ?? Edge(edge, snapshot, evidence);
                    edgesById[edge.Id] = edgeValue;
                    elements.Add(edgeValue);
                }
                var path = new PathValue(elements);
                paths.Add(path);
                stateRelationships.Add(relationship.Pattern.Binding,
                    relationship.Pattern.Hops.IsFixed ? elements[1] : path);
            }
            foreach (string binding in query.ReturnedBindings)
            {
                bindings.Add(new(binding, stateNodes!.TryGetValue(binding, out NodeValue? node) ? node :
                    stateRelationships.TryGetValue(binding, out QueryValue? matched) ? matched : UnboundValue.Instance));
            }

            matches.Add(new(bindings, paths));
        }

        var graph = new GraphResult(nodes.Values, edgesById.Values);
        var result = new QueryResultEnvelope(Guid.NewGuid(), new GraphPatternResult(graph, matches), evidence: evidence.Values,
            diagnostics: [new("GRAPH_SNAPSHOT", $"Matched at graph snapshot {snapshot.Value}.",
                QueryDiagnosticSeverity.Information)]);
        return result;
    }

    private static IReadOnlyList<GraphVertexId> SnapshotCandidates(GraphReadView view,
        GraphCandidateQuery candidates, CancellationToken cancellationToken = default)
    {
        if (candidates.Identities is { Count: > 0 })
        {
            return candidates.Identities.Where(view.Vertices.ContainsKey).Distinct().ToArray();
        }

        if (candidates.ScalarStringEquality is { } text &&
            view.FindScalarStringEquality(text, cancellationToken) is { } textCandidates)
        {
            return textCandidates;
        }

        if (candidates.ScalarNumericEquality is { } equality &&
            view.FindScalarNumericEquality(equality, cancellationToken) is { } equalCandidates)
        {
            return equalCandidates;
        }

        if (candidates.Property is not null && candidates.Value is not null)
        {
            return view.FindByProperty(candidates.Property, candidates.Value);
        }

        if (candidates.ScalarNumericBounds is { Count: > 0 } scalarBounds &&
            view.FindScalarNumericRange(scalarBounds, cancellationToken) is { } scalarRange)
        {
            return scalarRange;
        }

        if (candidates.NumericBounds is { Count: > 0 } bounds &&
            view.FindNumericCandidates(bounds, cancellationToken) is { } numeric)
        {
            return numeric;
        }

        if (candidates.Labels is not { Count: > 0 } labels)
        {
            return view.Vertices.Keys.ToArray();
        }

        if (labels.Count == 1)
        {
            return view.FindByLabel(labels[0]);
        }

        IReadOnlyList<GraphVertexId> narrowest = labels.Select(view.FindByLabel)
            .MinBy(items => items.Count)!;
        var selected = new List<GraphVertexId>(narrowest.Count);
        foreach (GraphVertexId id in narrowest)
        {
            GraphVertex? vertex = view.GetVertex(id);
            if (vertex is not null && labels.All(vertex.Labels.Contains))
            {
                selected.Add(id);
            }
        }
        return selected;
    }

    private static bool TryOrderedRootCandidates(BoundedGraphPatternQuery query, GraphReadView view, CancellationToken cancellationToken,
        out IReadOnlyList<GraphVertexId> roots)
    {
        roots = [];
        if (query.Relationships.Count != 0 || query.Filter is not null || query.Aggregation is not null ||
            query.OrderBy is not [GraphPatternOrderTerm order] || order.Binding != query.Start.Binding)
        {
            return false;
        }

        roots = view.OrderedByProperty(order.Property, order.Direction, cancellationToken);
        return true;
    }

    private static int Compare(GraphVertex left, GraphVertex right,
        IReadOnlyList<GraphPatternOrderTerm> orderBy)
    {
        foreach (GraphPatternOrderTerm term in orderBy)
        {
            bool leftPresent = left.Properties.TryGetValue(term.Property, out DatabaseValue? leftValue);
            bool rightPresent = right.Properties.TryGetValue(term.Property, out DatabaseValue? rightValue);
            if (leftPresent != rightPresent)
            {
                int presence = leftPresent ? -1 : 1;
                return term.Direction == GraphPatternSortDirection.Ascending ? presence : -presence;
            }

            if (!leftPresent)
            {
                continue;
            }

            int comparison = Compare(leftValue!, rightValue!);
            if (comparison != 0)
            {
                return term.Direction is GraphPatternSortDirection.Ascending
                ? comparison : -comparison;
            }
        }
        return left.Id.Value.CompareTo(right.Id.Value);
    }

    internal static IReadOnlyList<T> SelectOrdered<T>(IReadOnlyList<T> values, int offset, int limit,
        Comparison<T> comparison, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit == 0)
        {
            return [];
        }
        int required = checked(offset + limit);
        if (required >= values.Count / 2)
        {
            T[] ordered = values.ToArray();
            QueryComparisonSort.Sort(ordered, comparison, cancellationToken);
            return ordered.Skip(offset).Take(limit).ToArray();
        }

        var worstFirst = Comparer<T>.Create((left, right) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return comparison(right, left);
        });
        var selected = new PriorityQueue<T, T>(worstFirst);
        foreach (T value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selected.Count < required)
            {
                selected.Enqueue(value, value);
            }
            else if (comparison(value, selected.Peek()) < 0)
            {
                selected.Dequeue();
                selected.Enqueue(value, value);
            }
        }
        T[] result = selected.UnorderedItems.Select(item => item.Element).ToArray();
        QueryComparisonSort.Sort(result, comparison, cancellationToken);
        return result.Skip(offset).ToArray();
    }

    private static GraphPatternMatch SingleBindingMatch(string binding, QueryValue value) =>
        new([new(binding, value)], []);

    private static IReadOnlyList<FastCountGroup> SelectCountGroups(
        IReadOnlyList<FastCountGroup> groups, SingleGraphAggregation aggregation, CancellationToken cancellationToken) =>
        SelectOrdered(groups, aggregation.Offset, aggregation.Limit, (left, right) =>
        {
            int comparison = left.Count.CompareTo(right.Count);
            if (aggregation.Direction is GraphPatternSortDirection.Descending)
            {
                comparison = -comparison;
            }

            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Key, right.Key);
        }, cancellationToken);

    private static decimal? NumericProperty(MatchState state, string binding, string property)
    {
        if (state.VertexOrDefault(binding)?.Properties.GetValueOrDefault(property) is not { } value)
        {
            return null;
        }

        return NumericValue(value);
    }

    private static decimal? NumericValue(DatabaseValue? value) => value switch
    {
        DatabaseValue.Int64 integer => integer.Value,
        DatabaseValue.Decimal exact => exact.Value,
        DatabaseValue.Double number when Double.IsFinite(number.Value) => (decimal)number.Value,
        _ => null
    };

    private static DatabaseValue? BoundProperty(MatchState state, string binding, string property)
    {
        if (state.VertexOrDefault(binding)?.Properties.GetValueOrDefault(property) is { } vertexValue)
        {
            return vertexValue;
        }

        IReadOnlyList<GraphEdge> edges = state.Relationships
            .SingleOrDefault(item => item.Pattern.Binding == binding)?.Edges ?? [];
        return edges.Count == 1 ? edges[0].Properties.GetValueOrDefault(property) : null;
    }

    private static string BindingKey(MatchState state, string binding)
    {
        if (state.VertexOrDefault(binding) is { } vertex)
        {
            return $"v:{vertex.Id.Value:D}";
        }

        MatchedRelationship? relationship = state.Relationships.SingleOrDefault(item => item.Pattern.Binding == binding);
        return relationship is null || relationship.Vertices.Count == 0 ? "u:" :
            "e:" + String.Join('/', relationship.Edges.Select(edge => edge.Id.Value.ToString("D")));
    }

    private static int Compare(MatchState left, MatchState right, BoundedGraphPatternQuery query)
    {
        foreach (GraphPatternOrderTerm term in query.OrderBy)
        {
            if (term.Expression is { } expression)
            {
                QueryValue Value(MatchState state) => GraphScalarEvaluator.Evaluate(expression, state.PropertyValue);
                int computed = GraphScalarEvaluator.CompareForOrder(Value(left), Value(right));
                if (computed != 0)
                {
                    return term.Direction == GraphPatternSortDirection.Ascending ? computed : -computed;
                }
                continue;
            }
            DatabaseValue? leftValue = null;
            DatabaseValue? rightValue = null;
            bool leftPresent = left.VertexOrDefault(term.Binding)?.Properties.TryGetValue(term.Property,
                out leftValue) is true;
            bool rightPresent = right.VertexOrDefault(term.Binding)?.Properties.TryGetValue(term.Property,
                out rightValue) is true;
            if (leftPresent != rightPresent)
            {
                int presence = leftPresent ? -1 : 1;
                return term.Direction == GraphPatternSortDirection.Ascending ? presence : -presence;
            }

            if (!leftPresent)
            {
                continue;
            }

            int comparison = Compare(leftValue!, rightValue!);
            if (comparison != 0)
            {
                return term.Direction is GraphPatternSortDirection.Ascending
                ? comparison : -comparison;
            }
        }
        int tie = left.Vertex(query.Start.Binding).Id.Value.CompareTo(right.Vertex(query.Start.Binding).Id.Value);
        if (tie != 0)
        {
            return tie;
        }

        foreach (GraphPatternRelationship relationship in query.Relationships)
        {
            tie = Compare(left.EdgesOrEmpty(relationship.Binding), right.EdgesOrEmpty(relationship.Binding));
            if (tie != 0)
            {
                return tie;
            }

            tie = Compare(left.VertexOrDefault(relationship.Target.Binding)?.Id.Value,
                right.VertexOrDefault(relationship.Target.Binding)?.Id.Value);
            if (tie != 0)
            {
                return tie;
            }
        }
        return 0;
    }

    private static string ProjectionKey(MatchState state, BoundedGraphPatternQuery query) => String.Join('|',
        query.ReturnedBindings.Select(binding => state.VertexOrDefault(binding) is { } vertex
            ? $"v:{vertex.Id.Value:D}"
            : state.Relationships.SingleOrDefault(item => item.Pattern.Binding == binding) is { } relationship
                ? $"e:{String.Join(',', relationship.Edges.Select(edge => edge.Id.Value.ToString("D", CultureInfo.InvariantCulture)))}"
                : "u"));

    private static int Compare(Guid? left, Guid? right)
    {
        if (left.HasValue != right.HasValue)
        {
            return left.HasValue ? -1 : 1;
        }

        return left.HasValue ? left.Value.CompareTo(right!.Value) : 0;
    }

    private static int Compare(IReadOnlyList<GraphEdge> left, IReadOnlyList<GraphEdge> right)
    {
        int count = Math.Min(left.Count, right.Count);
        for (int index = 0; index < count; index++)
        {
            int comparison = left[index].Id.Value.CompareTo(right[index].Id.Value);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        if (left.Count == right.Count)
        {
            return 0;
        }

        if (left.Count == 0)
        {
            return 1;
        }

        if (right.Count == 0)
        {
            return -1;
        }

        return left.Count.CompareTo(right.Count);
    }

    private static IReadOnlyList<PathTraversal> Select(IReadOnlyList<PathTraversal> candidates,
        GraphPatternPathSelection selection, int count)
    {
        if (selection != GraphPatternPathSelection.All && count == 0)
        {
            return [];
        }
        if (selection is GraphPatternPathSelection.All || candidates.Count < 2)
        {
            return candidates;
        }

        return candidates.GroupBy(item => item.Current.Id).SelectMany(group =>
        {
            PathTraversal[] ordered = group.OrderBy(item => item.Edges.Count)
                .ThenBy(PathIdentity, StringComparer.Ordinal).ToArray();
            return selection is GraphPatternPathSelection.AnyShortest
                ? ordered.Take(count)
                : ordered.GroupBy(item => item.Edges.Count).Take(count).SelectMany(items => items);
        }).ToArray();
    }

    private static string PathIdentity(PathTraversal path) => String.Join('|',
        path.Edges.Select(item => item.Id.Value.ToString("N", CultureInfo.InvariantCulture)));

    private static int Compare(DatabaseValue left, DatabaseValue right) =>
        GraphDatabaseValueComparer.Instance.Compare(left, right);

    private static void EnsureWithinExpansionLimit(int actual, int maximum)
    {
        if (actual > maximum)
        {
            throw new GraphPatternExpansionLimitException(maximum);
        }
    }

    internal static IEnumerable<GraphVertex> ExchangeVertices(GraphReadView view, BoundedGraphPatternQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query.Relationships.Count == 0)
        {
            return SnapshotCandidates(view, CandidateQuery(query), cancellationToken).Select(view.GetVertex).OfType<GraphVertex>()
                .Where(v => Matches(v, query.Start) && (query.Filter is null || Matches(MatchState.Start(query.Start.Binding, v), query.Filter)));
        }
        // Quantified paths may traverse intermediate nodes that do not match the endpoint.
        if (query.Relationships.Any(r => !r.Hops.IsFixed))
        {
            return view.Vertices.Values;
        }

        var nodes = query.Relationships.Select(r => r.Target).Prepend(query.Start).ToArray();
        return view.Vertices.Values.Where(v => nodes.Any(n => Matches(v, n)));
    }

    private static bool SupportsIndependentDirectedSteps(BoundedGraphPatternQuery query)
    {
        if (query.Relationships.Any(r => r.Direction == GraphPatternDirection.Undirected))
        {
            return false;
        }
        // Independent-step optimizations cannot enforce relationship uniqueness
        // across a MATCH when two steps can consume the same physical edge.
        var required = query.Relationships.Where(r => r.MatchKind == GraphPatternMatchKind.Required).ToArray();
        for (int i = 0; i < required.Length; i++)
        {
            for (int j = i + 1; j < required.Length; j++)
            {
                if (required[i].Label is null || required[j].Label is null || required[i].Label == required[j].Label)
                {
                    return false;
                }
            }
        }
        return true;
    }

    internal static bool SupportsFrontierExchange(BoundedGraphPatternQuery query)
    {
        if (query.Relationships.Count == 0 || query.Relationships.Any(r => r.AllowRepeatedRelationships || r.PathMode != GraphPatternPathMode.Walk || r.Direction == GraphPatternDirection.Undirected || r.EdgePredicates.Count != 0) || !query.Start.Predicates.Any(p =>
                p.Operator == GraphPatternComparisonOperator.Equal && TryDatabaseValue(p.Value, out _)))
        {
            return false;
        }

        var bindings = new HashSet<string>(StringComparer.Ordinal) { query.Start.Binding };
        string source = query.Start.Binding;
        foreach (var relationship in query.Relationships)
        {
            if (!relationship.Hops.IsFixed || relationship.SourceBinding != source ||
                !bindings.Add(relationship.Target.Binding))
            {
                return false;
            }

            source = relationship.Target.Binding;
        }
        return true;
    }

    internal static IEnumerable<GraphVertex> ExchangeFrontierVertices(GraphReadView view,
        BoundedGraphPatternQuery query, int stage, IReadOnlyList<Guid>? identities)
    {
        if (!SupportsFrontierExchange(query) || stage < 0 || stage > query.Relationships.Count)
        {
            throw new ArgumentException("Unsupported graph frontier.");
        }
        // Only native node predicates are pushed. Global filters, row multiplicity,
        // optional matches and limits remain owned by the final pattern evaluator.
        return stage == 0
            ? SnapshotCandidates(view, CandidateQuery(query)).Select(view.GetVertex).OfType<GraphVertex>().Where(v => Matches(v, query.Start))
            : (identities ?? throw new ArgumentException("Missing frontier identities.")).Select(id => view.GetVertex(new(id)))
                .OfType<GraphVertex>().Where(v => Matches(v, query.Relationships[stage - 1].Target));
    }

    private static bool Matches(GraphVertex vertex, GraphPatternNode pattern) => Matches(vertex.Labels, vertex.Properties, pattern);

    private static bool MatchesEdgeProperties(GraphEdge edge, IReadOnlyList<GraphPatternPropertyPredicate> predicates)
    {
        foreach (var predicate in predicates)
        {
            if (!edge.Properties.TryGetValue(predicate.Property, out var value))
            {
                return false;
            }
            string operation = predicate.Operator switch
            {
                GraphPatternComparisonOperator.Equal => "=",
                GraphPatternComparisonOperator.NotEqual => "<>",
                GraphPatternComparisonOperator.LessThan => "<",
                GraphPatternComparisonOperator.LessThanOrEqual => "<=",
                GraphPatternComparisonOperator.GreaterThan => ">",
                GraphPatternComparisonOperator.GreaterThanOrEqual => ">=",
                _ => throw new ArgumentException("Unsupported edge property comparison.")
            };
            var expression = new GraphScalarExpression(null, null, null, operation,
                [new(null, null, QueryValue(value)), new(null, null, predicate.Value)]);
            if (GraphScalarEvaluator.Boolean(GraphScalarEvaluator.Evaluate(expression,
                (_, _) => throw new InvalidOperationException("An edge predicate must contain literal operands."))) != true)
            {
                return false;
            }
        }
        return true;
    }

    private static bool Matches(IReadOnlySet<string> labels, IReadOnlyDictionary<string, DatabaseValue> properties, GraphPatternNode pattern)
    {
        for (int index = 0; index < pattern.Labels.Count; index++)
        {
            if (!labels.Contains(pattern.Labels[index]))
            {
                return false;
            }
        }

        for (int index = 0; index < pattern.Predicates.Count; index++)
        {
            GraphPatternPropertyPredicate predicate = pattern.Predicates[index];
            if (!properties.TryGetValue(predicate.Property, out DatabaseValue? actual) ||
                !TryDatabaseValue(predicate.Value, out DatabaseValue? expected) ||
                !Matches(actual, expected!, predicate.Operator))
            {
                return false;
            }
        }
        return true;
    }

    private static bool Matches(DatabaseValue actual, DatabaseValue expected,
        GraphPatternComparisonOperator comparison)
    {
        if (actual.GetType() != expected.GetType())
        {
            return comparison is GraphPatternComparisonOperator.NotEqual;
        }

        int value = Compare(actual, expected);
        return comparison switch
        {
            GraphPatternComparisonOperator.Equal => value == 0,
            GraphPatternComparisonOperator.NotEqual => value != 0,
            GraphPatternComparisonOperator.LessThan => value < 0,
            GraphPatternComparisonOperator.LessThanOrEqual => value <= 0,
            GraphPatternComparisonOperator.GreaterThan => value > 0,
            GraphPatternComparisonOperator.GreaterThanOrEqual => value >= 0,
            _ => throw new ArgumentOutOfRangeException(nameof(comparison))
        };
    }

    private static bool Matches(MatchState state, GraphPatternFilter filter) => EvaluateFilter(state, filter) == true;

    private static bool? EvaluateFilter(MatchState state, GraphPatternFilter filter) => filter switch
    {
        GraphPatternExpressionFilter expression => EntityForge.Db.Graph.GraphScalarEvaluator.Boolean(
            EntityForge.Db.Graph.GraphScalarEvaluator.Evaluate(expression.Expression, state.PropertyValue)),
        GraphPatternNullTest test => (test.Property is null ? !state.IsBound(test.Binding)
            : state.PropertyValue(test.Binding, test.Property) is UnboundValue) != test.Negated,
        GraphPatternPropertyComparison comparison =>
            state.DatabaseProperty(comparison.Binding, comparison.Property) is { } actual
                && TryDatabaseValue(comparison.Value, out DatabaseValue? expected)
                ? Matches(actual, expected!, comparison.Operator) : null,
        GraphPatternBooleanFilter { Operator: GraphPatternBooleanOperator.And } boolean =>
            EvaluateFilter(state, boolean.Left) & EvaluateFilter(state, boolean.Right),
        GraphPatternBooleanFilter { Operator: GraphPatternBooleanOperator.Xor } boolean =>
            EvaluateFilter(state, boolean.Left) ^ EvaluateFilter(state, boolean.Right),
        GraphPatternBooleanFilter boolean => EvaluateFilter(state, boolean.Left) | EvaluateFilter(state, boolean.Right),
        GraphPatternNotFilter not => !EvaluateFilter(state, not.Operand),
        _ => throw new ArgumentOutOfRangeException(nameof(filter))
    };

    private NodeValue Node(GraphVertex vertex, DatabaseVersion snapshot,
        IDictionary<EvidenceReference, EvidenceRecord> evidence)
    {
        MaterializedNode materialized = MaterializeNode(vertex, snapshot);
        evidence.TryAdd(materialized.Evidence.Reference, materialized.Evidence);
        return materialized.Value;
    }

    private NodeValue AggregateNode(GraphVertex vertex, DatabaseVersion snapshot,
        ICollection<EvidenceRecord> evidence)
    {
        MaterializedNode materialized = MaterializeNode(vertex, snapshot);
        evidence.Add(materialized.Evidence);
        return materialized.Value;
    }

    private MaterializedNode MaterializeNode(GraphVertex vertex, DatabaseVersion snapshot)
    {
        var key = (snapshot.Value, vertex.Id);
        if (!_materializedNodes.TryGetValue(key, out MaterializedNode? materialized))
        {
            var reference = new EvidenceReference(new Uri(
                $"urn:entityforge:graph:evidence:vertex:{vertex.Id.Value:D}:version:{snapshot.Value}"));
            var record = new EvidenceRecord(reference, Provider,
                sourceRecordIdentifier: vertex.Id.Value.ToString("D"),
                snapshotIdentifier: snapshot.Value.ToString(CultureInfo.InvariantCulture));
            var value = new NodeValue(new IdentifierValue($"urn:uuid:{vertex.Id.Value:D}"),
                properties: vertex.Properties.Select(item => new PropertyAssertion(
                    Term("property", item.Key, SemanticTermRole.Property), QueryValue(item.Value),
                    evidence: [reference])), labels: vertex.Labels);
            materialized = new(value, record);
            if (_materializedNodes.Count >= MaximumMaterializedNodeCacheEntries)
            {
                _materializedNodes.Clear();
            }

            materialized = _materializedNodes.GetOrAdd(key, materialized);
        }
        return materialized;
    }

    private static EdgeValue Edge(GraphEdge edge, DatabaseVersion snapshot,
        IDictionary<EvidenceReference, EvidenceRecord> evidence)
    {
        EvidenceReference reference = Evidence("edge", edge.Id.Value, snapshot, evidence);
        return new(new IdentifierValue($"urn:uuid:{edge.From.Value:D}"),
            new IdentifierValue($"urn:uuid:{edge.To.Value:D}"),
            new RelationshipAssertion(Term("relationship", edge.Label, SemanticTermRole.Relationship),
                evidence: [reference]), new IdentifierValue($"urn:uuid:{edge.Id.Value:D}"),
            edge.Properties.Select(item => new PropertyAssertion(
                Term("property", item.Key, SemanticTermRole.Property), QueryValue(item.Value), evidence: [reference])));
    }

    private static EvidenceReference Evidence(string kind, Guid id, DatabaseVersion snapshot,
        IDictionary<EvidenceReference, EvidenceRecord> evidence)
    {
        var reference = new EvidenceReference(new Uri(
            $"urn:entityforge:graph:evidence:{kind}:{id:D}:version:{snapshot.Value}"));
        evidence.TryAdd(reference, new(reference, Provider, sourceRecordIdentifier: id.ToString("D"),
            snapshotIdentifier: snapshot.Value.ToString(CultureInfo.InvariantCulture)));
        return reference;
    }

    private static SemanticTerm Term(string kind, string value, SemanticTermRole role)
    {
        var key = (kind, value, role);
        if (Terms.TryGetValue(key, out SemanticTerm? term))
        {
            return term;
        }

        if (Terms.Count >= MaximumSemanticTermCacheEntries)
        {
            Terms.Clear();
        }

        return Terms.GetOrAdd(key, item => new(
            new Uri($"urn:entityforge:graph:{item.Kind}:{Uri.EscapeDataString(item.Value)}"), item.Role));
    }

    private static DatabaseValue DatabaseValue(QueryValue value) => TryDatabaseValue(value, out DatabaseValue? result)
        ? result! : throw new NotSupportedException($"Graph pattern literal '{value.GetType().Name}' is not supported.");

    private static bool TryDatabaseValue(QueryValue value, out DatabaseValue? result)
    {
        DatabaseValueConversion conversion = DatabaseValues.GetValue(value,
            static item => new(ConvertDatabaseValue(item)));
        result = conversion.Value;
        return result is not null;
    }

    private static DatabaseValue? ConvertDatabaseValue(QueryValue value) => value switch
    {
        LiteralValue { ScalarKind: QueryScalarKind.Date } item => new DatabaseValue.Date(EntityForge.Values.CalendarDate.Parse(item.LexicalValue)),
        LiteralValue { ScalarKind: QueryScalarKind.LocalTime } item => new DatabaseValue.LocalTime(EntityForge.Values.LocalTime.Parse(item.LexicalValue)),
        LiteralValue { ScalarKind: QueryScalarKind.Guid } item => new DatabaseValue.Uuid(Guid.Parse(item.LexicalValue)),
        LiteralValue { ScalarKind: QueryScalarKind.Decimal } item => new DatabaseValue.Decimal(Decimal.Parse(item.LexicalValue, CultureInfo.InvariantCulture)),
        LiteralValue { ScalarKind: QueryScalarKind.ZonedTime } item => new DatabaseValue.ZonedTime(EntityForge.Values.ZonedTime.Parse(item.LexicalValue)),
        LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime } item => new DatabaseValue.ZonedDateTime(EntityForge.Values.ZonedDateTime.Parse(item.LexicalValue)),
        LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } item => new DatabaseValue.LocalDateTime(EntityForge.Values.LocalDateTime.Parse(item.LexicalValue)),
        LiteralValue { ScalarKind: QueryScalarKind.Duration } item => new DatabaseValue.Duration(EntityForge.Values.CalendarDuration.Parse(item.LexicalValue), global::EntityForge.Db.DatabaseValue.Duration.QualifierFor(item.DatatypeIdentifier)),
        LiteralValue { ScalarKind: QueryScalarKind.Point } item => new DatabaseValue.Point(EntityForge.Values.SpatialPoint.Parse(item.LexicalValue)),
        LiteralValue { ScalarKind: QueryScalarKind.String } item => new DatabaseValue.String(item.LexicalValue),
        LiteralValue { ScalarKind: QueryScalarKind.Integer } item => new DatabaseValue.Int64(
            Int64.Parse(item.LexicalValue, CultureInfo.InvariantCulture)),
        LiteralValue { ScalarKind: QueryScalarKind.FloatingPoint } item => new DatabaseValue.Double(
            Double.Parse(item.LexicalValue, CultureInfo.InvariantCulture)),
        LiteralValue { ScalarKind: QueryScalarKind.Boolean } item => new DatabaseValue.Boolean(
            Boolean.Parse(item.LexicalValue)),
        IdentifierValue item when Uri.TryCreate(item.Identifier, UriKind.Absolute, out Uri? uri) =>
            new DatabaseValue.Uri(uri),
        _ => null
    };

    private sealed record DatabaseValueConversion(DatabaseValue? Value);

    private static GraphCandidateQuery CandidateQuery(BoundedGraphPatternQuery query)
        => CandidateQuery(query.Start, query.Filter);

    private static GraphCandidateQuery CandidateQuery(GraphPatternNode start, GraphPatternFilter? filter)
    {
        GraphPatternPropertyPredicate? indexed = start.Predicates
            .FirstOrDefault(item => item.Operator is GraphPatternComparisonOperator.Equal &&
                TryDatabaseValue(item.Value, out _));
        if (indexed is not null)
        {
            return new(Labels: start.Labels, Property: indexed.Property, Value: DatabaseValue(indexed.Value));
        }

        if (FindScalarNumericEquality(filter, start.Binding) is { } equality)
        {
            return new(Labels: start.Labels, ScalarNumericEquality: equality);
        }

        if (FindScalarStringEquality(filter, start.Binding) is { } text)
        {
            return new(Labels: start.Labels, ScalarStringEquality: text);
        }

        var scalarBounds = ScalarNumericBounds(filter, start.Binding).ToArray();
        if (scalarBounds.Length > 0)
        {
            return new(Labels: start.Labels, ScalarNumericBounds: scalarBounds.Where(b => b.Property == scalarBounds[0].Property).ToArray());
        }

        var numeric = start.Predicates.Where(p => p.Operator is GraphPatternComparisonOperator.LessThan or
            GraphPatternComparisonOperator.LessThanOrEqual or GraphPatternComparisonOperator.GreaterThan or
            GraphPatternComparisonOperator.GreaterThanOrEqual).Where(p =>
                TryDatabaseValue(p.Value, out var value) && value is EntityForge.Db.DatabaseValue.Int64 or EntityForge.Db.DatabaseValue.Double)
            .Select(p => new GraphNumericBound(p.Property, DatabaseValue(p.Value), p.Operator)).ToArray();
        return new(Labels: start.Labels, NumericBounds: numeric.Length == 0 ? null :
            numeric.Where(p => p.Property == numeric[0].Property && p.Value.GetType() == numeric[0].Value.GetType()).ToArray());
    }

    private static GraphScalarNumericEquality? FindScalarNumericEquality(GraphPatternFilter? filter, string binding) => filter switch
    {
        GraphPatternBooleanFilter { Operator: GraphPatternBooleanOperator.And } both =>
            FindScalarNumericEquality(both.Left, binding) ?? FindScalarNumericEquality(both.Right, binding),
        GraphPatternExpressionFilter expression => FindScalarNumericEquality(expression.Expression, binding),
        _ => null
    };

    private static GraphScalarNumericEquality? FindScalarNumericEquality(GraphScalarExpression expression, string binding)
    {
        if (expression is { Function: "AND", Arguments: [var left, var right] })
        {
            return FindScalarNumericEquality(left, binding) ?? FindScalarNumericEquality(right, binding);
        }
        if (expression is not { Function: "=", Arguments: [var first, var second] })
        {
            return null;
        }
        if (first.Literal is not null)
        {
            (first, second) = (second, first);
        }
        return first.Binding == binding && first.Property is { } property && second.Literal is LiteralValue
        { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint } value
                ? new(property, value) : null;
    }

    private static QueryValue QueryValue(DatabaseValue value) => value switch
    {
        DatabaseValue.String item => new LiteralValue(item.Value),
        DatabaseValue.Int64 item => new LiteralValue(item.Value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer),
        DatabaseValue.Double item => new LiteralValue(item.Value.ToString("R", CultureInfo.InvariantCulture), QueryScalarKind.FloatingPoint),
        DatabaseValue.Boolean item => new LiteralValue(item.Value ? "true" : "false", QueryScalarKind.Boolean),
        DatabaseValue.Date calendar => new LiteralValue(calendar.Value.ToString(), QueryScalarKind.Date),
        DatabaseValue.Duration duration => new LiteralValue(duration.Value.ToString(), QueryScalarKind.Duration, duration.DatatypeIdentifier),
        DatabaseValue.LocalDateTime time => new LiteralValue(time.Value.ToString(), QueryScalarKind.LocalDateTime),
        DatabaseValue.LocalTime time => new LiteralValue(time.Value.ToString(), QueryScalarKind.LocalTime),
        DatabaseValue.Decimal exact => new LiteralValue(exact.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), QueryScalarKind.Decimal),
        DatabaseValue.Uuid uuid => new LiteralValue(uuid.Value.ToString("D"), QueryScalarKind.Guid),
        DatabaseValue.ZonedTime time => new LiteralValue(time.Value.ToString(), QueryScalarKind.ZonedTime),
        DatabaseValue.ZonedDateTime time => new LiteralValue(time.Value.ToString(), QueryScalarKind.ZonedDateTime),
        DatabaseValue.Point point => new LiteralValue(point.Value.ToString(), QueryScalarKind.Point),
        DatabaseValue.DateTimeOffset item => new LiteralValue(item.Value.ToString("O", CultureInfo.InvariantCulture), QueryScalarKind.DateTime),
        DatabaseValue.Uri item => new IdentifierValue(item.Value.AbsoluteUri),
        DatabaseValue.Binary item => new LiteralValue(Convert.ToBase64String(item.Value.Span)),
        DatabaseValue.Json item => new JsonValue(item.Utf8Json),
        DatabaseValue.Array item => new ListValue(item.Value.Select(QueryValue)),
        _ => throw new NotSupportedException($"Graph value '{value.Kind}' is not supported.")
    };

    private sealed record MatchState(
        IReadOnlyDictionary<string, GraphVertex?> Vertices,
        IReadOnlyList<MatchedRelationship> Relationships)
    {
        public GraphVertex Vertex(string binding) => Vertices[binding]!;

        public GraphVertex? VertexOrDefault(string binding) => Vertices.GetValueOrDefault(binding);

        public bool IsBound(string binding) => VertexOrDefault(binding) is not null ||
            Relationships.Any(item => item.Pattern.Binding == binding && item.Vertices.Count != 0);

        public QueryValue PropertyValue(string binding, string property) => DatabaseProperty(binding, property) is { } value
            ? QueryValue(value) : UnboundValue.Instance;

        public DatabaseValue? DatabaseProperty(string binding, string property)
        {
            IReadOnlyDictionary<string, DatabaseValue>? properties = VertexOrDefault(binding)?.Properties;
            if (properties is null)
            {
                var relationship = Relationships.SingleOrDefault(item => item.Pattern.Binding == binding);
                if (relationship?.Edges.Count == 1)
                {
                    properties = relationship.Edges[0].Properties;
                }
            }
            return properties?.GetValueOrDefault(property);
        }

        public IReadOnlyList<GraphEdge> EdgesOrEmpty(string binding) => Relationships.Single(item =>
            item.Pattern.Binding == binding).Edges;

        public static MatchState Start(string binding, GraphVertex vertex) =>
            new(new RootVertexBinding(binding, vertex), []);

        public MatchState Append(GraphPatternRelationship relationship, IReadOnlyList<GraphEdge> edges,
            IReadOnlyList<GraphVertex> pathVertices, bool introducesTarget)
        {
            var boundVertices = new Dictionary<string, GraphVertex?>(Vertices, StringComparer.Ordinal);
            if (introducesTarget)
            {
                boundVertices.Add(relationship.Target.Binding, pathVertices[^1]);
            }

            return new(boundVertices, [.. Relationships, new(relationship, edges, pathVertices)]);
        }

        public MatchState AppendUnbound(GraphPatternRelationship relationship, bool introducesTarget)
        {
            var vertices = new Dictionary<string, GraphVertex?>(Vertices, StringComparer.Ordinal);
            if (introducesTarget)
            {
                vertices.Add(relationship.Target.Binding, null);
            }

            return new(vertices, [.. Relationships, new(relationship, [], [])]);
        }
    }

    private sealed record PathTraversal(GraphVertex Current, IReadOnlyList<GraphEdge> Edges,
        IReadOnlyList<GraphVertex> Vertices);

    private sealed record MatchedRelationship(GraphPatternRelationship Pattern,
        IReadOnlyList<GraphEdge> Edges, IReadOnlyList<GraphVertex> Vertices);

    private sealed class FastCountGroup(string key, GraphVertex? vertex, HashSet<string>? distinctValues)
    {
        public string Key { get; } = key;

        public GraphVertex? Vertex { get; } = vertex;

        public HashSet<string>? DistinctValues { get; } = distinctValues;

        public long Count
        {
            get; set;
        }
    }

    private sealed record MaterializedNode(NodeValue Value, EvidenceRecord Evidence);

    private sealed class GraphDatabaseValueEqualityComparer : IEqualityComparer<DatabaseValue>
    {
        public static GraphDatabaseValueEqualityComparer Instance { get; } = new();

        public bool Equals(DatabaseValue? left, DatabaseValue? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null || left.GetType() != right.GetType())
            {
                return false;
            }

            return (left, right) switch
            {
                (DatabaseValue.String first, DatabaseValue.String second) =>
                    StringComparer.Ordinal.Equals(first.Value, second.Value),
                (DatabaseValue.Date first, DatabaseValue.Date second) => first.Value == second.Value,
                (DatabaseValue.Duration first, DatabaseValue.Duration second) => first.Qualifier == second.Qualifier && first.Value == second.Value,
                (DatabaseValue.LocalDateTime first, DatabaseValue.LocalDateTime second) => first.Value == second.Value,
                (DatabaseValue.LocalTime first, DatabaseValue.LocalTime second) => first.Value == second.Value,
                (DatabaseValue.Uuid first, DatabaseValue.Uuid second) => first.Value == second.Value,
                (DatabaseValue.Decimal first, DatabaseValue.Decimal second) => first.Value == second.Value,
                (DatabaseValue.ZonedTime first, DatabaseValue.ZonedTime second) => first.Value == second.Value,
                (DatabaseValue.ZonedDateTime first, DatabaseValue.ZonedDateTime second) => first.Value == second.Value,
                (DatabaseValue.Point first, DatabaseValue.Point second) => first.Value == second.Value,
                (DatabaseValue.Int64 first, DatabaseValue.Int64 second) => first.Value == second.Value,
                (DatabaseValue.Double first, DatabaseValue.Double second) => first.Value.Equals(second.Value),
                (DatabaseValue.Boolean first, DatabaseValue.Boolean second) => first.Value == second.Value,
                (DatabaseValue.DateTimeOffset first, DatabaseValue.DateTimeOffset second) =>
                    first.Value.Equals(second.Value),
                (DatabaseValue.Uri first, DatabaseValue.Uri second) =>
                    StringComparer.Ordinal.Equals(first.Value.AbsoluteUri, second.Value.AbsoluteUri),
                (DatabaseValue.Binary first, DatabaseValue.Binary second) =>
                    first.Value.Span.SequenceEqual(second.Value.Span),
                (DatabaseValue.Json first, DatabaseValue.Json second) =>
                    StringComparer.Ordinal.Equals(first.Value.Value, second.Value.Value),
                (DatabaseValue.Array first, DatabaseValue.Array second) => first.Equals(second),
                _ => false
            };
        }

        public int GetHashCode(DatabaseValue value) => value switch
        {
            DatabaseValue.String item => HashCode.Combine(item.Kind,
                StringComparer.Ordinal.GetHashCode(item.Value)),
            DatabaseValue.Date calendar => HashCode.Combine(calendar.Kind, calendar.Value),
            DatabaseValue.Duration duration => HashCode.Combine(duration.Kind, duration.Qualifier, duration.Value),
            DatabaseValue.LocalDateTime time => HashCode.Combine(time.Kind, time.Value),
            DatabaseValue.LocalTime time => HashCode.Combine(time.Kind, time.Value),
            DatabaseValue.Uuid uuid => HashCode.Combine(uuid.Kind, uuid.Value),
            DatabaseValue.Decimal exact => HashCode.Combine(exact.Kind, exact.Value),
            DatabaseValue.ZonedTime time => HashCode.Combine(time.Kind, time.Value),
            DatabaseValue.ZonedDateTime time => HashCode.Combine(time.Kind, time.Value),
            DatabaseValue.Point point => HashCode.Combine(point.Kind, point.Value),
            DatabaseValue.Int64 item => HashCode.Combine(item.Kind, item.Value),
            DatabaseValue.Double item => HashCode.Combine(item.Kind, item.Value),
            DatabaseValue.Boolean item => HashCode.Combine(item.Kind, item.Value),
            DatabaseValue.DateTimeOffset item => HashCode.Combine(item.Kind, item.Value),
            DatabaseValue.Uri item => HashCode.Combine(item.Kind,
                StringComparer.Ordinal.GetHashCode(item.Value.AbsoluteUri)),
            DatabaseValue.Binary item => BinaryHashCode(item),
            DatabaseValue.Json item => HashCode.Combine(item.Kind,
                StringComparer.Ordinal.GetHashCode(item.Value.Value)),
            DatabaseValue.Array item => item.GetHashCode(),
            _ => throw new NotSupportedException($"Graph equality does not support '{value.Kind}' values.")
        };

        private static int BinaryHashCode(DatabaseValue.Binary value)
        {
            var hash = new HashCode();
            hash.Add(value.Kind);
            foreach (byte item in value.Value.Span)
            {
                hash.Add(item);
            }

            return hash.ToHashCode();
        }
    }
}

internal sealed record BoundedGraphPatternExecution(
    QueryResultEnvelope Result,
    int CandidateCount,
    TimeSpan CandidateAcquisitionDuration,
    IReadOnlyList<GraphPatternRelationshipExecution> Relationships,
    int ExpansionCount,
    int RowsBeforeLimit,
    TimeSpan? SortDuration,
    TimeSpan ProjectionDuration);

internal sealed record GraphPatternRelationshipExecution(
    int OriginalIndex,
    int InputMatches,
    int InspectedEdges,
    int EnumeratedPaths,
    int DiscardedPaths,
    int OutputMatches,
    int PreservedRows,
    TimeSpan Duration);

internal sealed record GraphRelationshipPlanEntry(
    int OriginalIndex,
    GraphPatternRelationship Pattern,
    double? EstimatedCost,
    bool Reorderable);
