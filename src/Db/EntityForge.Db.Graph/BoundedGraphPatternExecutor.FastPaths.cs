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

internal sealed partial class BoundedGraphPatternExecutor
{
    private bool TryExecuteRootAggregate(BoundedGraphPatternQuery query, DatabaseVersion snapshot,
        IReadOnlyList<GraphVertexId> roots, TimeSpan candidateDuration, CancellationToken cancellationToken,
        out BoundedGraphPatternExecution? execution)
    {
        execution = null;
        if (HasConvergingBindings(query) || query.Relationships.Any(relationship => relationship.Filter is not null))
        {
            return false;
        }

        if (SingleAggregation(query.Aggregation) is not { GroupBinding: null } aggregation ||
            query.Relationships.Count != 0 || query.Filter is not null || query.OrderBy.Count != 0 ||
            aggregation.ValueBinding is not null && aggregation.ValueBinding != query.Start.Binding)
        {
            return false;
        }

        if (aggregation.Function is not GraphPatternAggregateFunction.Count &&
            (aggregation.ValueBinding != query.Start.Binding || aggregation.ValueProperty is null))
        {
            return false;
        }

        // Numeric promotion and cross-type DISTINCT must use the shared aggregate
        // evaluator; the count-only shortcut does not inspect scalar values.
        if (aggregation.Function != GraphPatternAggregateFunction.Count ||
            aggregation.Distinct && aggregation.ValueProperty is not null)
        {
            return false;
        }

        GraphReadView view = capturedView ?? runtime!.CaptureReadView(snapshot);
        int inputRows = 0, expansionCount = 0;
        bool nullEliminated = false;
        decimal sum = 0;
        decimal? minimum = null, maximum = null;
        long numericCount = 0, count = 0;
        HashSet<DatabaseValue>? distinctCountValues = aggregation.Distinct &&
            aggregation.Function is GraphPatternAggregateFunction.Count && aggregation.ValueProperty is not null
            ? new(GraphDatabaseValueEqualityComparer.Instance) : null;
        HashSet<decimal>? distinctNumericValues = aggregation.Distinct &&
            aggregation.Function is not GraphPatternAggregateFunction.Count ? [] : null;
        bool candidatesFullyMatchStart = query.Start.Predicates.Count == 0 && query.Start.Labels.Count <= 1;
        long started = Stopwatch.GetTimestamp();
        foreach (GraphVertexId rootId in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphVertex? root = view.GetVertex(rootId);
            if (root is null || !candidatesFullyMatchStart && !Matches(root, query.Start))
            {
                continue;
            }

            if (++inputRows > aggregation.MaximumInputRows)
            {
                throw new GraphPatternExpansionLimitException(aggregation.MaximumInputRows);
            }

            EnsureWithinExpansionLimit(++expansionCount, query.MaximumExpansions);
            if (aggregation.Function is GraphPatternAggregateFunction.Count)
            {
                if (aggregation.ValueProperty is null)
                {
                    if (!aggregation.Distinct || aggregation.ValueBinding is not null || count == 0)
                    {
                        count++;
                    }

                    continue;
                }
                if (!root.Properties.TryGetValue(aggregation.ValueProperty, out DatabaseValue? value))
                {
                    nullEliminated = true;
                    continue;
                }

                if (distinctCountValues is null || distinctCountValues.Add(value))
                {
                    count++;
                }

                continue;
            }
            decimal? numeric = NumericValue(root.Properties.GetValueOrDefault(aggregation.ValueProperty!));
            if (numeric is null || distinctNumericValues is not null && !distinctNumericValues.Add(numeric.Value))
            {
                continue;
            }

            sum += numeric.Value;
            numericCount++;
            minimum = minimum is null ? numeric : Math.Min(minimum.Value, numeric.Value);
            maximum = maximum is null ? numeric : Math.Max(maximum.Value, numeric.Value);
        }
        decimal? aggregateValue = inputRows == 0 ? EmptyAggregate(aggregation.Function) : aggregation.Function switch
        {
            GraphPatternAggregateFunction.Count => count,
            GraphPatternAggregateFunction.Sum => numericCount == 0 ? null : sum,
            GraphPatternAggregateFunction.Minimum => minimum,
            GraphPatternAggregateFunction.Maximum => maximum,
            GraphPatternAggregateFunction.Average => numericCount == 0 ? null : sum / numericCount,
            _ => throw new ArgumentOutOfRangeException()
        };
        QueryValue valueResult = aggregateValue is null ? UnboundValue.Instance : new LiteralValue(
            aggregateValue.Value.ToString(CultureInfo.InvariantCulture),
            aggregation.Function is GraphPatternAggregateFunction.Count
                ? QueryScalarKind.Integer : QueryScalarKind.Decimal);
        RecordRow[] rows = aggregation.Offset == 0 && aggregation.Limit > 0
            ? [new RecordRow([new(aggregation.Alias, valueResult)])] : [];
        var envelope = new QueryResultEnvelope(Guid.NewGuid(),
            new RecordResult([new(aggregation.Alias)], rows), diagnostics: new[]
            {
                new QueryResultDiagnostic("GRAPH_SNAPSHOT", $"Aggregated at graph snapshot {snapshot.Value}.", QueryDiagnosticSeverity.Information)
            }
                .Concat(nullEliminated ? [NullEliminatedDiagnostic] : []));
        execution = new(envelope, roots.Count, candidateDuration, [], expansionCount, inputRows,
            null, Stopwatch.GetElapsedTime(started));
        return true;
    }

    private bool TryExecuteTwoBranchCount(BoundedGraphPatternQuery query, DatabaseVersion snapshot,
        IReadOnlyList<GraphVertexId> roots, TimeSpan candidateDuration, CancellationToken cancellationToken,
        out BoundedGraphPatternExecution? execution)
    {
        execution = null;
        if (HasConvergingBindings(query) || query.Relationships.Any(relationship => relationship.Filter is not null))
        {
            return false;
        }

        if (SingleAggregation(query.Aggregation) is not
            {
                Function: GraphPatternAggregateFunction.Count,
                Distinct: false,
                GroupBinding: not null,
                ValueBinding: not null,
                ValueProperty: null
            } aggregation || query.Filter is not null || query.Relationships.Count != 2 ||
            query.Start.Labels is not [var rootLabel] || query.Start.Predicates.Count != 0)
        {
            return false;
        }

        GraphPatternRelationship? grouping = UniqueRelationship(query.Relationships, relationship =>
            relationship.SourceBinding == query.Start.Binding &&
            relationship.Target.Binding == aggregation.GroupBinding);
        GraphPatternRelationship? counted = UniqueRelationship(query.Relationships, relationship =>
            relationship.SourceBinding == query.Start.Binding &&
            relationship.Target.Binding == aggregation.ValueBinding);
        if (grouping is null || counted is null || ReferenceEquals(grouping, counted) ||
            !IsSimpleRequiredHop(grouping) || !IsSimpleRequiredHop(counted) ||
            grouping.Label is null || counted.Label is null ||
            grouping.Target.Labels is not [var groupLabel] || grouping.Target.Predicates.Count != 0 ||
            counted.Target.Labels is not [var countedLabel] || counted.Target.Predicates.Count != 0)
        {
            return false;
        }

        int groupingIndex = ReferenceEquals(query.Relationships[0], grouping) ? 0 : 1;
        int countedIndex = 1 - groupingIndex;

        long started = Stopwatch.GetTimestamp();
        GraphReadView view = capturedView ?? runtime!.CaptureReadView(snapshot);
        int eligibleRoots = 0;
        foreach (GraphVertexId id in roots)
        {
            if (view.GetVertex(id) is { } root && root.Labels.Contains(rootLabel))
            {
                eligibleRoots++;
            }
        }

        GraphTwoBranchCounts cached = view.TwoBranchCounts(new(rootLabel,
            grouping.Label, grouping.Direction, groupLabel,
            counted.Label, counted.Direction, countedLabel));
        if (eligibleRoots != cached.Roots)
        {
            return false;
        }

        var inspected = new int[2];
        inspected[groupingIndex] = cached.GroupingEdges;
        inspected[countedIndex] = cached.CountedEdges;
        int expansionCount = checked(cached.Roots + cached.GroupingEdges + cached.CountedEdges);
        EnsureWithinExpansionLimit(expansionCount, query.MaximumExpansions);
        int matchedRows = cached.MatchedRows;
        if (matchedRows > aggregation.MaximumInputRows)
        {
            throw new GraphPatternExpansionLimitException(aggregation.MaximumInputRows);
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        long projectionStarted = Stopwatch.GetTimestamp();
        var evidence = new List<EvidenceRecord>();
        IReadOnlyList<GraphTwoBranchCount> ordered = view.OrderedTwoBranchCounts(new(rootLabel,
            grouping.Label, grouping.Direction, groupLabel,
            counted.Label, counted.Direction, countedLabel), aggregation.Direction);
        RecordRow[] resultRows = ordered.Skip(aggregation.Offset).Take(aggregation.Limit).Select(group =>
            new RecordRow([
                new(aggregation.GroupBinding, AggregateNode(view.GetVertex(group.Target)!, snapshot, evidence)),
                new(aggregation.Alias, new LiteralValue(group.Count.ToString(CultureInfo.InvariantCulture),
                    QueryScalarKind.Integer))
            ])).ToArray();
        var envelope = new QueryResultEnvelope(Guid.NewGuid(),
            new RecordResult([new(aggregation.GroupBinding), new(aggregation.Alias)], resultRows),
            evidence: evidence, diagnostics: [new("GRAPH_SNAPSHOT",
                $"Aggregated at graph snapshot {snapshot.Value}.", QueryDiagnosticSeverity.Information)]);
        GraphPatternRelationshipExecution[] analyses = query.Relationships.Select((relationship, index) =>
            new GraphPatternRelationshipExecution(index, eligibleRoots, inspected[index],
                index == countedIndex ? matchedRows : cached.Counts.Count, 0,
                index == countedIndex ? matchedRows : cached.Counts.Count, 0, elapsed)).ToArray();
        execution = new(envelope, roots.Count, candidateDuration, analyses, expansionCount, matchedRows,
            null, Stopwatch.GetElapsedTime(projectionStarted));
        return true;
    }

    private static GraphPatternRelationship? UniqueRelationship(
        IReadOnlyList<GraphPatternRelationship> relationships, Func<GraphPatternRelationship, bool> predicate)
    {
        GraphPatternRelationship? selected = null;
        foreach (GraphPatternRelationship relationship in relationships)
        {
            if (!predicate(relationship))
            {
                continue;
            }

            if (selected is not null)
            {
                return null;
            }

            selected = relationship;
        }
        return selected;
    }

    private static bool HasConvergingBindings(BoundedGraphPatternQuery query)
    {
        HashSet<string> bound = new(StringComparer.Ordinal) { query.Start.Binding };
        return query.Relationships.Any(relationship => !bound.Add(relationship.Target.Binding));
    }

    private static decimal? EmptyAggregate(GraphPatternAggregateFunction function) => function switch
    {
        GraphPatternAggregateFunction.Count => 0,
        GraphPatternAggregateFunction.Sum => null,
        GraphPatternAggregateFunction.Minimum or GraphPatternAggregateFunction.Maximum or
            GraphPatternAggregateFunction.Average => null,
        _ => throw new ArgumentOutOfRangeException(nameof(function))
    };

    private static bool IsSimpleRequiredHop(GraphPatternRelationship relationship) =>
        relationship.Hops == GraphPatternHopRange.Fixed &&
        relationship.MatchKind is GraphPatternMatchKind.Required &&
        relationship.PathSelection is GraphPatternPathSelection.All;

    private bool TryExecuteTwoBranchOrdered(BoundedGraphPatternQuery query, DatabaseVersion snapshot,
        IReadOnlyList<GraphVertexId> roots, TimeSpan candidateDuration,
        out BoundedGraphPatternExecution? execution)
    {
        execution = null;
        if (HasConvergingBindings(query) || query.Relationships.Any(relationship => relationship.Filter is not null))
        {
            return false;
        }

        if (SingleAggregation(query.Aggregation) is not null || query.Filter is not null || query.Distinct ||
            query.Relationships.Count != 2 || query.OrderBy is not [GraphPatternOrderTerm order] ||
            query.ReturnedBindings is not [var returned] || order.Binding != returned ||
            query.Start.Labels is not [var rootLabel] || query.Start.Predicates.Count != 0)
        {
            return false;
        }

        GraphPatternRelationship? counted = UniqueRelationship(query.Relationships, item =>
            item.SourceBinding == query.Start.Binding && item.Target.Binding == returned);
        GraphPatternRelationship? grouping = UniqueRelationship(query.Relationships, item =>
            item.SourceBinding == query.Start.Binding && item.Target.Binding != returned);
        if (counted is null || grouping is null || ReferenceEquals(counted, grouping) ||
            !IsSimpleRequiredHop(counted) || !IsSimpleRequiredHop(grouping) ||
            counted.Label is null || grouping.Label is null ||
            counted.Target.Labels is not [var countedLabel] || counted.Target.Predicates.Count != 0 ||
            grouping.Target.Labels is not [var groupingLabel] || grouping.Target.Predicates.Count != 0)
        {
            return false;
        }

        GraphReadView view = capturedView ?? runtime!.CaptureReadView(snapshot);
        int eligibleRoots = roots.Count(id => view.GetVertex(id)?.Labels.Contains(rootLabel) == true);
        GraphTwoBranchTargetCounts cached = view.TwoBranchTargetCounts(new(rootLabel,
            grouping.Label, grouping.Direction, groupingLabel,
            counted.Label, counted.Direction, countedLabel));
        if (eligibleRoots != cached.Roots)
        {
            return false;
        }

        int required = checked(query.Offset + query.MaximumMatches);
        var retained = new List<GraphVertex>(Math.Min(required, cached.MatchedRows));
        foreach (GraphVertexId id in view.OrderedByProperty(order.Property, order.Direction))
        {
            if (!cached.Counts.TryGetValue(id, out int multiplicity))
            {
                continue;
            }

            GraphVertex vertex = view.GetVertex(id)!;
            while (multiplicity-- > 0 && retained.Count < required)
            {
                retained.Add(vertex);
            }

            if (retained.Count == required)
            {
                break;
            }
        }
        GraphVertex[] selected = retained.Skip(query.Offset).ToArray();
        int expansionCount = checked(cached.Roots + cached.GroupingEdges + cached.CountedEdges);
        EnsureWithinExpansionLimit(expansionCount, query.MaximumExpansions);
        long projectionStarted = Stopwatch.GetTimestamp();
        var evidence = new List<EvidenceRecord>();
        var nodesById = new Dictionary<GraphVertexId, NodeValue>();
        foreach (GraphVertex vertex in selected)
        {
            if (!nodesById.ContainsKey(vertex.Id))
            {
                nodesById.Add(vertex.Id, AggregateNode(vertex, snapshot, evidence));
            }
        }

        var envelope = new QueryResultEnvelope(Guid.NewGuid(),
            new GraphPatternResult(new(nodesById.Values, []),
                selected.Select(vertex => SingleBindingMatch(returned, nodesById[vertex.Id]))),
            evidence: evidence, diagnostics: [new("GRAPH_SNAPSHOT",
                $"Matched at graph snapshot {snapshot.Value}.", QueryDiagnosticSeverity.Information)]);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(projectionStarted);
        GraphPatternRelationshipExecution[] analyses = query.Relationships.Select((item, index) =>
            ReferenceEquals(item, grouping)
                ? new GraphPatternRelationshipExecution(index, cached.Roots,
                    cached.GroupingEdges, cached.GroupingEdges, 0, cached.GroupingEdges, 0, elapsed)
                : new GraphPatternRelationshipExecution(index, cached.Roots,
                    cached.CountedEdges, cached.MatchedRows, 0, cached.MatchedRows, 0, elapsed)).ToArray();
        execution = new(envelope, roots.Count, candidateDuration, analyses, expansionCount,
            cached.MatchedRows, elapsed, elapsed);
        return true;
    }

    private bool TryExecuteFixedLinearOrdered(BoundedGraphPatternQuery query, DatabaseVersion snapshot,
        GraphQueryStatistics statistics,
        IReadOnlyList<GraphVertexId> roots, TimeSpan candidateDuration, CancellationToken cancellationToken,
        out BoundedGraphPatternExecution? execution)
    {
        execution = null;
        if (HasConvergingBindings(query) || query.Relationships.Any(relationship => relationship.Filter is not null))
        {
            return false;
        }

        if (SingleAggregation(query.Aggregation) is not null || query.Filter is not null || query.Distinct ||
            query.Relationships.Count < 2 || query.OrderBy is not [GraphPatternOrderTerm order] ||
            query.ReturnedBindings is not [var returned] || query.Relationships.Any(relationship =>
                !relationship.Hops.IsFixed || relationship.MatchKind is not GraphPatternMatchKind.Required ||
                relationship.PathSelection is not GraphPatternPathSelection.All))
        {
            return false;
        }

        IReadOnlyList<GraphRelationshipPlanEntry> relationshipPlan = PlanRelationships(query, statistics);
        var available = new HashSet<string>(StringComparer.Ordinal) { query.Start.Binding };
        foreach (GraphRelationshipPlanEntry planned in relationshipPlan)
        {
            GraphPatternRelationship relationship = planned.Pattern;
            if (!available.Contains(relationship.SourceBinding))
            {
                return false;
            }

            available.Add(relationship.Target.Binding);
        }
        if (!available.Contains(returned) || order.Binding != returned)
        {
            return false;
        }

        GraphReadView view = capturedView ?? runtime!.CaptureReadView(snapshot);
        var vertices = new Dictionary<string, GraphVertex>(StringComparer.Ordinal);
        var inspected = new int[query.Relationships.Count];
        var inputs = new int[query.Relationships.Count];
        var outputs = new int[query.Relationships.Count];
        int required = checked(query.Offset + query.MaximumMatches);
        var matchMultiplicities = new Dictionary<GraphVertexId, int>();
        int expansionCount = 0, matchedRows = 0;
        long started = Stopwatch.GetTimestamp();

        void Visit(int index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index == query.Relationships.Count)
            {
                matchedRows++;
                GraphVertex value = vertices[returned];
                matchMultiplicities[value.Id] = matchMultiplicities.GetValueOrDefault(value.Id) + 1;
                return;
            }
            GraphPatternRelationship relationship = relationshipPlan[index].Pattern;
            if (!vertices.TryGetValue(relationship.SourceBinding, out GraphVertex? source))
            {
                return;
            }

            inputs[index]++;
            foreach (GraphEdge edge in relationship.Direction is GraphPatternDirection.Outgoing
                         ? view.Outgoing(source.Id, null)
                         : view.Incoming(source.Id, null))
            {
                if (relationship.Label is not null && edge.Label != relationship.Label)
                {
                    continue;
                }

                inspected[index]++;
                EnsureWithinExpansionLimit(++expansionCount, query.MaximumExpansions);
                GraphVertexId targetId = relationship.Direction is GraphPatternDirection.Outgoing ? edge.To : edge.From;
                GraphVertex? target = view.GetVertex(targetId);
                if (target is null || !Matches(target, relationship.Target))
                {
                    continue;
                }

                bool introduced = !vertices.TryGetValue(relationship.Target.Binding, out GraphVertex? requiredTarget);
                if (!introduced && requiredTarget!.Id != target.Id)
                {
                    continue;
                }

                if (introduced)
                {
                    vertices.Add(relationship.Target.Binding, target);
                }

                outputs[index]++;
                Visit(index + 1);
                if (introduced)
                {
                    vertices.Remove(relationship.Target.Binding);
                }
            }
        }

        foreach (GraphVertexId rootId in roots)
        {
            GraphVertex? root = view.GetVertex(rootId);
            if (root is null || !Matches(root, query.Start))
            {
                continue;
            }

            EnsureWithinExpansionLimit(++expansionCount, query.MaximumExpansions);
            vertices.Add(query.Start.Binding, root);
            Visit(0);
            vertices.Clear();
        }
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        var retained = new List<GraphVertex>(required);
        foreach (GraphVertexId id in view.OrderedByProperty(order.Property, order.Direction, cancellationToken))
        {
            if (!matchMultiplicities.TryGetValue(id, out int multiplicity))
            {
                continue;
            }

            GraphVertex vertex = view.GetVertex(id)!;
            while (multiplicity-- > 0 && retained.Count < required)
            {
                retained.Add(vertex);
            }

            if (retained.Count == required)
            {
                break;
            }
        }
        GraphVertex[] selected = retained.Skip(query.Offset).ToArray();
        long projectionStarted = Stopwatch.GetTimestamp();
        var evidence = new List<EvidenceRecord>();
        var nodesById = new Dictionary<GraphVertexId, NodeValue>();
        foreach (GraphVertex vertex in selected)
        {
            if (!nodesById.ContainsKey(vertex.Id))
            {
                nodesById.Add(vertex.Id, AggregateNode(vertex, snapshot, evidence));
            }
        }

        var envelope = new QueryResultEnvelope(Guid.NewGuid(),
            new GraphPatternResult(new(nodesById.Values, []), selected.Select(vertex =>
                SingleBindingMatch(returned, nodesById[vertex.Id]))),
            evidence: evidence, diagnostics: [new("GRAPH_SNAPSHOT",
                $"Matched at graph snapshot {snapshot.Value}.", QueryDiagnosticSeverity.Information)]);
        GraphPatternRelationshipExecution[] analyses = relationshipPlan.Select((planned, index) =>
            new GraphPatternRelationshipExecution(planned.OriginalIndex, inputs[index], inspected[index], outputs[index], 0,
                outputs[index], 0, elapsed)).ToArray();
        execution = new(envelope, roots.Count, candidateDuration, analyses, expansionCount, matchedRows,
            elapsed, Stopwatch.GetElapsedTime(projectionStarted));
        return true;
    }

    private bool TryExecuteFixedLinearProjection(BoundedGraphPatternQuery query, DatabaseVersion snapshot,
        GraphQueryStatistics statistics,
        IReadOnlyList<GraphVertexId> roots, TimeSpan candidateDuration, CancellationToken cancellationToken,
        out BoundedGraphPatternExecution? execution)
    {
        execution = null;
        if (HasConvergingBindings(query) || query.Relationships.Any(relationship => relationship.Filter is not null))
        {
            return false;
        }

        if (SingleAggregation(query.Aggregation) is not null || query.Filter is not null || query.Distinct ||
            query.OrderBy.Count != 0 || query.Relationships.Count < 2 ||
            query.ReturnedBindings is not [var returned] || query.Relationships.Any(relationship =>
                !relationship.Hops.IsFixed || relationship.MatchKind is not GraphPatternMatchKind.Required ||
                relationship.PathSelection is not GraphPatternPathSelection.All))
        {
            return false;
        }

        IReadOnlyList<GraphRelationshipPlanEntry> relationshipPlan = PlanRelationships(query, statistics);
        var available = new HashSet<string>(StringComparer.Ordinal) { query.Start.Binding };
        foreach (GraphRelationshipPlanEntry planned in relationshipPlan)
        {
            if (!available.Contains(planned.Pattern.SourceBinding))
            {
                return false;
            }

            available.Add(planned.Pattern.Target.Binding);
        }
        if (!available.Contains(returned))
        {
            return false;
        }

        GraphReadView view = capturedView ?? runtime!.CaptureReadView(snapshot);
        var vertices = new Dictionary<string, GraphVertex>(StringComparer.Ordinal);
        var selected = new List<GraphVertex>(Math.Min(query.MaximumMatches, 256));
        var inspected = new int[query.Relationships.Count];
        var inputs = new int[query.Relationships.Count];
        var outputs = new int[query.Relationships.Count];
        int required = checked(query.Offset + query.MaximumMatches);
        int expansionCount = 0, matchedRows = 0;
        long started = Stopwatch.GetTimestamp();

        bool Visit(int index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index == relationshipPlan.Count)
            {
                if (matchedRows++ >= query.Offset)
                {
                    selected.Add(vertices[returned]);
                }

                return matchedRows >= required;
            }
            GraphPatternRelationship relationship = relationshipPlan[index].Pattern;
            if (!vertices.TryGetValue(relationship.SourceBinding, out GraphVertex? source))
            {
                return false;
            }

            inputs[index]++;
            foreach (GraphEdge edge in relationship.Direction is GraphPatternDirection.Outgoing
                         ? view.Outgoing(source.Id, null) : view.Incoming(source.Id, null))
            {
                if (relationship.Label is not null && edge.Label != relationship.Label)
                {
                    continue;
                }

                inspected[index]++;
                EnsureWithinExpansionLimit(++expansionCount, query.MaximumExpansions);
                GraphVertexId targetId = relationship.Direction is GraphPatternDirection.Outgoing ? edge.To : edge.From;
                GraphVertex? target = view.GetVertex(targetId);
                if (target is null || !Matches(target, relationship.Target))
                {
                    continue;
                }

                bool introduced = !vertices.TryGetValue(relationship.Target.Binding, out GraphVertex? requiredTarget);
                if (!introduced && requiredTarget!.Id != target.Id)
                {
                    continue;
                }

                if (introduced)
                {
                    vertices.Add(relationship.Target.Binding, target);
                }

                outputs[index]++;
                bool complete = Visit(index + 1);
                if (introduced)
                {
                    vertices.Remove(relationship.Target.Binding);
                }

                if (complete)
                {
                    return true;
                }
            }
            return false;
        }

        foreach (GraphVertexId rootId in roots)
        {
            GraphVertex? root = view.GetVertex(rootId);
            if (root is null || !Matches(root, query.Start))
            {
                continue;
            }

            EnsureWithinExpansionLimit(++expansionCount, query.MaximumExpansions);
            vertices.Add(query.Start.Binding, root);
            bool complete = Visit(0);
            vertices.Clear();
            if (complete)
            {
                break;
            }
        }
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        long projectionStarted = Stopwatch.GetTimestamp();
        var evidence = new List<EvidenceRecord>();
        var nodesById = new Dictionary<GraphVertexId, NodeValue>();
        foreach (GraphVertex vertex in selected)
        {
            if (!nodesById.ContainsKey(vertex.Id))
            {
                nodesById.Add(vertex.Id, AggregateNode(vertex, snapshot, evidence));
            }
        }

        var envelope = new QueryResultEnvelope(Guid.NewGuid(),
            new GraphPatternResult(new(nodesById.Values, []), selected.Select(vertex =>
                SingleBindingMatch(returned, nodesById[vertex.Id]))),
            evidence: evidence, diagnostics: [new("GRAPH_SNAPSHOT",
                $"Matched at graph snapshot {snapshot.Value}.", QueryDiagnosticSeverity.Information)]);
        GraphPatternRelationshipExecution[] analyses = relationshipPlan.Select((planned, index) =>
            new GraphPatternRelationshipExecution(planned.OriginalIndex, inputs[index], inspected[index],
                outputs[index], 0, outputs[index], 0, elapsed)).ToArray();
        execution = new(envelope, roots.Count, candidateDuration, analyses, expansionCount,
            matchedRows, null, Stopwatch.GetElapsedTime(projectionStarted));
        return true;
    }

    private bool TryExecuteFixedOneHopProjection(BoundedGraphPatternQuery query, DatabaseVersion snapshot,
        IReadOnlyList<GraphVertexId> roots, TimeSpan candidateDuration, CancellationToken cancellationToken,
        out BoundedGraphPatternExecution? execution)
    {
        execution = null;
        if (HasConvergingBindings(query) || query.Relationships.Any(relationship => relationship.Filter is not null))
        {
            return false;
        }

        if (SingleAggregation(query.Aggregation) is not null || query.Filter is not null || query.Distinct ||
            query.OrderBy.Count != 0 || query.Relationships is not [var relationship] ||
            query.ReturnedBindings is not [var returned] ||
            relationship.SourceBinding != query.Start.Binding ||
            relationship.Target.Binding != returned || returned == query.Start.Binding ||
            !relationship.Hops.IsFixed ||
            relationship.MatchKind is not GraphPatternMatchKind.Required ||
            relationship.PathSelection is not GraphPatternPathSelection.All)
        {
            return false;
        }

        GraphReadView view = capturedView ?? runtime!.CaptureReadView(snapshot);
        int required = checked(query.Offset + query.MaximumMatches);
        var selected = new List<GraphVertex>(Math.Min(required, 256));
        int eligibleRoots = 0, inspectedEdges = 0, matchedRows = 0, expansionCount = 0;
        long started = Stopwatch.GetTimestamp();
        foreach (GraphVertexId rootId in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphVertex? root = view.GetVertex(rootId);
            if (root is null || !Matches(root, query.Start))
            {
                continue;
            }

            eligibleRoots++;
            EnsureWithinExpansionLimit(++expansionCount, query.MaximumExpansions);
            IReadOnlyList<GraphEdge> edges = relationship.Direction is GraphPatternDirection.Outgoing
                ? view.Outgoing(root.Id, relationship.Label)
                : view.Incoming(root.Id, relationship.Label);
            foreach (GraphEdge edge in edges)
            {
                inspectedEdges++;
                EnsureWithinExpansionLimit(++expansionCount, query.MaximumExpansions);
                GraphVertexId targetId = relationship.Direction is GraphPatternDirection.Outgoing
                    ? edge.To : edge.From;
                GraphVertex? target = view.GetVertex(targetId);
                if (target is null || !Matches(target, relationship.Target))
                {
                    continue;
                }

                if (matchedRows++ >= query.Offset && selected.Count < query.MaximumMatches)
                {
                    selected.Add(target);
                }
            }
        }
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        long projectionStarted = Stopwatch.GetTimestamp();
        var evidence = new List<EvidenceRecord>();
        var nodes = new Dictionary<GraphVertexId, NodeValue>();
        foreach (GraphVertex vertex in selected)
        {
            if (!nodes.TryGetValue(vertex.Id, out NodeValue? node))
            {
                nodes.Add(vertex.Id, node = AggregateNode(vertex, snapshot, evidence));
            }
        }
        var envelope = new QueryResultEnvelope(Guid.NewGuid(),
            new GraphPatternResult(new(nodes.Values, []), selected.Select(vertex =>
                SingleBindingMatch(returned, nodes[vertex.Id]))), evidence: evidence,
            diagnostics: [new("GRAPH_SNAPSHOT", $"Matched at graph snapshot {snapshot.Value}.",
                QueryDiagnosticSeverity.Information)]);
        execution = new(envelope, roots.Count, candidateDuration,
            [new(0, eligibleRoots, inspectedEdges, matchedRows, 0, matchedRows, 0, elapsed)],
            expansionCount, matchedRows, null, Stopwatch.GetElapsedTime(projectionStarted));
        return true;
    }

    private bool TryExecuteFixedLinearCount(BoundedGraphPatternQuery query, DatabaseVersion snapshot,
        GraphQueryStatistics statistics,
        IReadOnlyList<GraphVertexId> roots, TimeSpan candidateDuration, CancellationToken cancellationToken,
        out BoundedGraphPatternExecution? execution)
    {
        execution = null;
        if (HasConvergingBindings(query) || query.Relationships.Any(relationship => relationship.Filter is not null))
        {
            return false;
        }

        if (SingleAggregation(query.Aggregation) is not { Function: GraphPatternAggregateFunction.Count, ValueProperty: null } aggregation || query.Filter is not null ||
            query.Relationships.Count < 2 || query.Relationships.Any(relationship =>
                !relationship.Hops.IsFixed || relationship.MatchKind is not GraphPatternMatchKind.Required ||
                relationship.PathSelection is not GraphPatternPathSelection.All))
        {
            return false;
        }

        IReadOnlyList<GraphRelationshipPlanEntry> relationshipPlan = PlanRelationships(query, statistics);
        var available = new HashSet<string>(StringComparer.Ordinal) { query.Start.Binding };
        foreach (GraphRelationshipPlanEntry planned in relationshipPlan)
        {
            GraphPatternRelationship relationship = planned.Pattern;
            if (!available.Contains(relationship.SourceBinding))
            {
                return false;
            }

            available.Add(relationship.Target.Binding);
        }
        if (aggregation.GroupBinding is not null && !available.Contains(aggregation.GroupBinding) ||
            aggregation.ValueBinding is not null && !available.Contains(aggregation.ValueBinding) &&
            !query.Relationships.Any(item => item.Binding == aggregation.ValueBinding))
        {
            return false;
        }

        long started = Stopwatch.GetTimestamp();
        var groups = new Dictionary<GraphVertexId, FastCountGroup>();
        FastCountGroup? ungrouped = null;
        GraphReadView view = capturedView ?? runtime!.CaptureReadView(snapshot);
        var vertices = new Dictionary<string, GraphVertex>(StringComparer.Ordinal);
        var edges = new Dictionary<string, GraphEdge>(StringComparer.Ordinal);
        var inspected = new int[query.Relationships.Count];
        var inputs = new int[query.Relationships.Count];
        var outputs = new int[query.Relationships.Count];
        int expansionCount = 0, matchedRows = 0;

        void Accumulate()
        {
            if (++matchedRows > aggregation.MaximumInputRows)
            {
                throw new GraphPatternExpansionLimitException(aggregation.MaximumInputRows);
            }

            GraphVertex? groupVertex = aggregation.GroupBinding is null
                ? null : vertices.GetValueOrDefault(aggregation.GroupBinding);
            FastCountGroup? group;
            if (groupVertex is null)
            {
                group = ungrouped ??= new(String.Empty, null, aggregation.Distinct
                    ? new HashSet<string>(StringComparer.Ordinal) : null);
            }
            else if (!groups.TryGetValue(groupVertex.Id, out group))
            {
                group = new($"v:{groupVertex.Id.Value:D}", groupVertex, aggregation.Distinct
                    ? new HashSet<string>(StringComparer.Ordinal) : null);
                groups.Add(groupVertex.Id, group);
            }
            bool countBound = aggregation.ValueBinding is null ||
                vertices.ContainsKey(aggregation.ValueBinding) || edges.ContainsKey(aggregation.ValueBinding);
            if (!countBound)
            {
                return;
            }

            if (group.DistinctValues is null)
            {
                group.Count++;
            }
            else
            {
                string countKey = aggregation.ValueBinding switch
                {
                    null => "*",
                    var binding when vertices.TryGetValue(binding, out GraphVertex? vertex) =>
                        $"v:{vertex.Id.Value:D}",
                    var binding when edges.TryGetValue(binding, out GraphEdge? edge) =>
                        $"e:{edge.Id.Value:D}",
                    _ => throw new InvalidOperationException("The count binding was not available.")
                };
                if (group.DistinctValues.Add(countKey))
                {
                    group.Count++;
                }
            }
        }

        void Visit(int index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index == query.Relationships.Count)
            {
                Accumulate();
                return;
            }
            GraphPatternRelationship relationship = relationshipPlan[index].Pattern;
            if (!vertices.TryGetValue(relationship.SourceBinding, out GraphVertex? source))
            {
                return;
            }

            inputs[index]++;
            foreach (GraphEdge edge in relationship.Direction is GraphPatternDirection.Outgoing
                         ? view.Outgoing(source.Id, null)
                         : view.Incoming(source.Id, null))
            {
                if (relationship.Label is not null && edge.Label != relationship.Label)
                {
                    continue;
                }

                inspected[index]++;
                EnsureWithinExpansionLimit(++expansionCount, query.MaximumExpansions);
                GraphVertexId targetId = relationship.Direction is GraphPatternDirection.Outgoing ? edge.To : edge.From;
                GraphVertex? target = view.GetVertex(targetId);
                if (target is null || !Matches(target, relationship.Target))
                {
                    continue;
                }

                bool introduced = !vertices.TryGetValue(relationship.Target.Binding, out GraphVertex? required);
                if (!introduced && required!.Id != target.Id)
                {
                    continue;
                }

                if (introduced)
                {
                    vertices.Add(relationship.Target.Binding, target);
                }

                edges.Add(relationship.Binding, edge);
                outputs[index]++;
                Visit(index + 1);
                edges.Remove(relationship.Binding);
                if (introduced)
                {
                    vertices.Remove(relationship.Target.Binding);
                }
            }
        }

        int eligibleRoots = 0;
        foreach (GraphVertexId rootId in roots)
        {
            GraphVertex? root = view.GetVertex(rootId);
            if (root is null || !Matches(root, query.Start))
            {
                continue;
            }

            eligibleRoots++;
            EnsureWithinExpansionLimit(++expansionCount, query.MaximumExpansions);
            vertices.Add(query.Start.Binding, root);
            Visit(0);
            vertices.Clear();
        }
        if (groups.Count == 0 && ungrouped is null && aggregation.GroupBinding is null)
        {
            ungrouped = new(String.Empty, null, null);
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        long projectionStarted = Stopwatch.GetTimestamp();
        List<FastCountGroup> ordered = ungrouped is null
            ? groups.Values.ToList() : groups.Values.Append(ungrouped).ToList();
        IReadOnlyList<FastCountGroup> selectedGroups = SelectCountGroups(ordered, aggregation, cancellationToken);
        var evidence = new List<EvidenceRecord>(selectedGroups.Count);
        RecordRow[] resultRows = selectedGroups.Select(group =>
        {
            QueryValue aggregateValue = new LiteralValue(
                group.Count.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
            return aggregation.GroupBinding is null
                ? new RecordRow([new(aggregation.Alias, aggregateValue)])
                : new RecordRow([new(aggregation.GroupBinding, group.Vertex is null
                    ? UnboundValue.Instance : AggregateNode(group.Vertex, snapshot, evidence)),
                    new(aggregation.Alias, aggregateValue)]);
        }).ToArray();
        RecordField[] fields = aggregation.GroupBinding is null
            ? [new(aggregation.Alias)] : [new(aggregation.GroupBinding), new(aggregation.Alias)];
        var envelope = new QueryResultEnvelope(Guid.NewGuid(), new RecordResult(fields, resultRows),
            evidence: evidence, diagnostics: [new("GRAPH_SNAPSHOT",
                $"Aggregated at graph snapshot {snapshot.Value}.", QueryDiagnosticSeverity.Information)]);
        GraphPatternRelationshipExecution[] analyses = relationshipPlan.Select((planned, index) =>
            new GraphPatternRelationshipExecution(planned.OriginalIndex, inputs[index], inspected[index], outputs[index], 0,
                outputs[index], 0, elapsed)).ToArray();
        execution = new(envelope, roots.Count, candidateDuration, analyses, expansionCount,
            matchedRows, null, Stopwatch.GetElapsedTime(projectionStarted));
        return true;
    }

    private bool TryExecuteFixedOneHopCount(BoundedGraphPatternQuery query, DatabaseVersion snapshot,
        IReadOnlyList<GraphVertexId> roots, TimeSpan candidateDuration, CancellationToken cancellationToken,
        out BoundedGraphPatternExecution? execution)
    {
        execution = null;
        if (HasConvergingBindings(query) || query.Relationships.Any(relationship => relationship.Filter is not null))
        {
            return false;
        }

        if (SingleAggregation(query.Aggregation) is not { Function: GraphPatternAggregateFunction.Count, ValueProperty: null } aggregation || query.Filter is not null ||
            query.Relationships is not [GraphPatternRelationship relationship] ||
            relationship.SourceBinding != query.Start.Binding || !relationship.Hops.IsFixed ||
            relationship.MatchKind is not GraphPatternMatchKind.Required ||
            relationship.PathSelection is not GraphPatternPathSelection.All)
        {
            return false;
        }

        long relationshipStarted = Stopwatch.GetTimestamp();
        int inputMatches = 0, inspectedEdges = 0, matchedRows = 0;
        int expansionCount = 0;
        var groups = new Dictionary<GraphVertexId, FastCountGroup>();
        FastCountGroup? ungrouped = null;
        GraphReadView view = capturedView ?? runtime!.CaptureReadView(snapshot);
        string? sourceLabel = query.Start.Labels.Count == 1 ? query.Start.Labels[0] : null;
        string? targetLabel = relationship.Target.Labels.Count == 1 ? relationship.Target.Labels[0] : null;
        bool indexedCounts = aggregation is { Distinct: false } &&
            aggregation.GroupBinding == relationship.Target.Binding &&
            aggregation.ValueBinding == query.Start.Binding && relationship.Label is not null &&
            relationship.Hops == GraphPatternHopRange.Fixed && sourceLabel is not null &&
            query.Start.Predicates.Count == 0 && targetLabel is not null &&
            relationship.Target.Predicates.Count == 0;
        if (indexedCounts)
        {
            return ExecuteIndexedOneHopCount(query, snapshot, roots, candidateDuration, view,
                relationship, aggregation, sourceLabel!, targetLabel!, relationshipStarted, out execution);
        }
        else
        {
            var eligibleRoots = new Dictionary<GraphVertexId, GraphVertex>();
            foreach (GraphVertexId rootId in roots)
            {
                GraphVertex? root = view.GetVertex(rootId);
                if (root is not null && Matches(root, query.Start))
                {
                    eligibleRoots[rootId] = root;
                }
            }
            inputMatches = eligibleRoots.Count;
            expansionCount = inputMatches;
            EnsureWithinExpansionLimit(expansionCount, query.MaximumExpansions);

            void Accumulate(GraphVertex root, GraphEdge edge)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureWithinExpansionLimit(++expansionCount, query.MaximumExpansions);
                inspectedEdges++;
                GraphVertexId targetId = relationship.Direction is GraphPatternDirection.Outgoing ? edge.To : edge.From;
                GraphVertex? target = view.GetVertex(targetId);
                if (target is null || !Matches(target, relationship.Target))
                {
                    return;
                }

                if (++matchedRows > aggregation.MaximumInputRows)
                {
                    throw new GraphPatternExpansionLimitException(aggregation.MaximumInputRows);
                }

                GraphVertex? groupVertex = aggregation.GroupBinding switch
                {
                    null => null,
                    var binding when binding == query.Start.Binding => root,
                    var binding when binding == relationship.Target.Binding => target,
                    _ => throw new NotSupportedException("A fixed-hop aggregate has an unsupported group binding.")
                };
                FastCountGroup? group;
                if (groupVertex is null)
                {
                    group = ungrouped ??= new(String.Empty, null, aggregation.Distinct
                        ? new HashSet<string>(StringComparer.Ordinal) : null);
                }
                else if (!groups.TryGetValue(groupVertex.Id, out group))
                {
                    group = new($"v:{groupVertex.Id.Value:D}", groupVertex, aggregation.Distinct
                        ? new HashSet<string>(StringComparer.Ordinal) : null);
                    groups.Add(groupVertex.Id, group);
                }
                bool countBound = aggregation.ValueBinding is null ||
                    aggregation.ValueBinding == query.Start.Binding ||
                    aggregation.ValueBinding == relationship.Target.Binding ||
                    aggregation.ValueBinding == relationship.Binding;
                if (!countBound)
                {
                    return;
                }

                if (group.DistinctValues is null)
                {
                    group.Count++;
                }
                else
                {
                    string countKey = aggregation.ValueBinding switch
                    {
                        null => "*",
                        var binding when binding == query.Start.Binding => $"v:{root.Id.Value:D}",
                        var binding when binding == relationship.Target.Binding => $"v:{target.Id.Value:D}",
                        var binding when binding == relationship.Binding => $"e:{edge.Id.Value:D}",
                        _ => throw new InvalidOperationException("The count binding was not available.")
                    };
                    if (group.DistinctValues.Add(countKey))
                    {
                        group.Count++;
                    }
                }
            }

            if (eligibleRoots.Count >= MinimumRootsForAggregateEdgeScan)
            {
                foreach (GraphEdge edge in view.Edges.Values)
                {
                    if (relationship.Label is not null && edge.Label != relationship.Label)
                    {
                        continue;
                    }

                    GraphVertexId sourceId = relationship.Direction is GraphPatternDirection.Outgoing ? edge.From : edge.To;
                    if (eligibleRoots.TryGetValue(sourceId, out GraphVertex? root))
                    {
                        Accumulate(root, edge);
                    }
                }
            }
            else
            {
                foreach (GraphVertex root in eligibleRoots.Values)
                {
                    foreach (GraphEdge edge in relationship.Direction is GraphPatternDirection.Outgoing
                                 ? view.Outgoing(root.Id, null)
                                 : view.Incoming(root.Id, null))
                    {
                        if (relationship.Label is not null && edge.Label != relationship.Label)
                        {
                            continue;
                        }

                        Accumulate(root, edge);
                    }
                }
            }
        }
        if (groups.Count == 0 && ungrouped is null && aggregation.GroupBinding is null)
        {
            ungrouped = new(String.Empty, null, null);
        }

        TimeSpan relationshipDuration = Stopwatch.GetElapsedTime(relationshipStarted);
        long projectionStarted = Stopwatch.GetTimestamp();
        List<FastCountGroup> ordered = ungrouped is null
            ? groups.Values.ToList() : groups.Values.Append(ungrouped).ToList();
        IReadOnlyList<FastCountGroup> selectedGroups = SelectCountGroups(ordered, aggregation, cancellationToken);
        var evidence = new List<EvidenceRecord>(selectedGroups.Count);
        RecordRow[] resultRows = selectedGroups.Select(group =>
        {
            QueryValue aggregateValue = new LiteralValue(
                group.Count.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
            return aggregation.GroupBinding is null
                ? new RecordRow([new(aggregation.Alias, aggregateValue)])
                : new RecordRow([new(aggregation.GroupBinding, group.Vertex is null
                    ? UnboundValue.Instance : AggregateNode(group.Vertex, snapshot, evidence)),
                    new(aggregation.Alias, aggregateValue)]);
        }).ToArray();
        RecordField[] fields = aggregation.GroupBinding is null
            ? [new(aggregation.Alias)] : [new(aggregation.GroupBinding), new(aggregation.Alias)];
        var envelope = new QueryResultEnvelope(Guid.NewGuid(), new RecordResult(fields, resultRows),
            evidence: evidence, diagnostics: [new("GRAPH_SNAPSHOT",
                $"Aggregated at graph snapshot {snapshot.Value}.", QueryDiagnosticSeverity.Information)]);
        execution = new(envelope, roots.Count, candidateDuration,
            [new(0, inputMatches, inspectedEdges, matchedRows, 0, matchedRows, 0, relationshipDuration)],
            expansionCount, matchedRows, null, Stopwatch.GetElapsedTime(projectionStarted));
        return true;
    }

    private bool TryExecuteRootOnly(BoundedGraphPatternQuery query, DatabaseVersion snapshot,
        IReadOnlyList<GraphVertexId> roots, TimeSpan candidateDuration, bool candidatesOrdered,
        out BoundedGraphPatternExecution? execution)
    {
        execution = null;
        if (HasConvergingBindings(query) || query.Relationships.Any(relationship => relationship.Filter is not null))
        {
            return false;
        }

        if (query.Relationships.Count != 0 || query.Filter is not null || SingleAggregation(query.Aggregation) is not null ||
            query.ReturnedBindings is not [var returned] || returned != query.Start.Binding)
        {
            return false;
        }

        GraphReadView view = capturedView ?? runtime!.CaptureReadView(snapshot);
        List<GraphVertex> vertices;
        int rowsBeforeLimit;
        if (candidatesOrdered)
        {
            int firstRetained = query.Offset;
            int retainedUntil = checked(query.Offset + query.MaximumMatches);
            int matched = 0;
            vertices = new(Math.Min(query.MaximumMatches, roots.Count));
            foreach (GraphVertexId id in roots)
            {
                GraphVertex? vertex = view.GetVertex(id);
                if (vertex is null || !Matches(vertex, query.Start))
                {
                    continue;
                }

                if (matched >= firstRetained && matched < retainedUntil)
                {
                    vertices.Add(vertex);
                }

                matched++;
                if (matched == retainedUntil)
                {
                    break;
                }
            }
            rowsBeforeLimit = matched;
        }
        else
        {
            vertices = roots.Select(view.GetVertex).OfType<GraphVertex>()
                .Where(vertex => Matches(vertex, query.Start)).ToList();
            rowsBeforeLimit = vertices.Count;
        }
        EnsureWithinExpansionLimit(rowsBeforeLimit, query.MaximumExpansions);
        TimeSpan? sortDuration = null;
        if (query.OrderBy.Count > 0 && !candidatesOrdered)
        {
            long sortStarted = Stopwatch.GetTimestamp();
            int required = checked(query.Offset + query.MaximumMatches);
            vertices = vertices.OrderBy(vertex => vertex,
                    Comparer<GraphVertex>.Create((left, right) => Compare(left, right, query.OrderBy)))
                .Take(required).ToList();
            sortDuration = Stopwatch.GetElapsedTime(sortStarted);
        }
        if (!candidatesOrdered)
        {
            vertices = vertices.Skip(query.Offset).Take(query.MaximumMatches).ToList();
        }

        long projectionStarted = Stopwatch.GetTimestamp();
        var evidence = new Dictionary<EvidenceReference, EvidenceRecord>();
        NodeValue[] nodes = vertices.Select(vertex => Node(vertex, snapshot, evidence)).ToArray();
        GraphPatternMatch[] matches = nodes.Select(node => new GraphPatternMatch(
            [new(query.Start.Binding, (QueryValue)node)], [])).ToArray();
        var envelope = new QueryResultEnvelope(Guid.NewGuid(), new GraphPatternResult(new(nodes, []), matches),
            evidence: evidence.Values, diagnostics: [new("GRAPH_SNAPSHOT",
                $"Matched at graph snapshot {snapshot.Value}.", QueryDiagnosticSeverity.Information)]);
        execution = new(envelope, roots.Count, candidateDuration, [], rowsBeforeLimit,
            rowsBeforeLimit, sortDuration, Stopwatch.GetElapsedTime(projectionStarted));
        return true;
    }

    private bool ExecuteIndexedOneHopCount(BoundedGraphPatternQuery query, DatabaseVersion snapshot,
        IReadOnlyList<GraphVertexId> roots, TimeSpan candidateDuration, GraphReadView view,
        GraphPatternRelationship relationship, SingleGraphAggregation aggregation,
        string sourceLabel, string targetLabel, long relationshipStarted,
        out BoundedGraphPatternExecution? execution)
    {
        GraphLabeledRelationshipCounts indexed = view.LabeledRelationshipCounts(
            relationship.Label!, sourceLabel!, targetLabel!, relationship.Direction);
        // The indexed-count fast path excludes start predicates and requires exactly one source label.
        // Candidate planning has therefore already returned the complete matching source-label set.
        int inputMatches = roots.Count;
        int matchedRows = 0;
        foreach (int count in indexed.Counts.Values)
        {
            matchedRows = checked(matchedRows + count);
        }

        if (matchedRows > aggregation.MaximumInputRows)
        {
            throw new GraphPatternExpansionLimitException(aggregation.MaximumInputRows);
        }

        int inspectedEdges = indexed.InspectedEdges;
        int expansionCount = checked(inputMatches + inspectedEdges);
        EnsureWithinExpansionLimit(expansionCount, query.MaximumExpansions);

        IReadOnlyList<GraphLabeledRelationshipCount> indexedOrder = view.OrderedLabeledRelationshipCounts(
            relationship.Label!, sourceLabel!, targetLabel!, relationship.Direction,
            aggregation.Direction);
        long indexedProjectionStarted = Stopwatch.GetTimestamp();
        var indexedEvidence = new List<EvidenceRecord>(Math.Min(aggregation.Limit, indexedOrder.Count));
        RecordRow[] indexedRows = indexedOrder.Skip(aggregation.Offset).Take(aggregation.Limit).Select(group =>
        {
            GraphVertex target = view.GetVertex(group.Target)!;
            return new RecordRow([
                new(aggregation.GroupBinding!, AggregateNode(target, snapshot, indexedEvidence)),
                new(aggregation.Alias, group.Value)
            ]);
        }).ToArray();
        var indexedEnvelope = new QueryResultEnvelope(Guid.NewGuid(), new RecordResult(
                [new(aggregation.GroupBinding!), new(aggregation.Alias)], indexedRows),
            evidence: indexedEvidence, diagnostics: [new("GRAPH_SNAPSHOT",
                $"Aggregated at graph snapshot {snapshot.Value}.", QueryDiagnosticSeverity.Information)]);
        execution = new(indexedEnvelope, roots.Count, candidateDuration,
            [new(0, inputMatches, inspectedEdges, matchedRows, 0, matchedRows, 0,
                Stopwatch.GetElapsedTime(relationshipStarted))], expansionCount, matchedRows, null,
            Stopwatch.GetElapsedTime(indexedProjectionStarted));
        return true;
    }
}
