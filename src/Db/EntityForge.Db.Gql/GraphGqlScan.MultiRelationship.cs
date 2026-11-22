using System.Runtime.CompilerServices;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlScan
{
    private static async IAsyncEnumerable<GqlScanPage> ExecuteMultiRelationshipScanAsync(CompiledGraphGqlQuery compiled,
        GraphGqlQueryRequest request, IReadOnlyList<GraphPatternOrderTerm> ordering, IGraphDatabase database,
        GqlScanBudget budget, long offset, long limit, [EnumeratorCancellation] CancellationToken cancellation)
    {
        var query = compiled.Query;
        if (compiled.Query.Aggregation is not null || compiled.Pipeline?.Stages.Any(stage => stage.Aggregation is not null) == true)
        {
            if (database is not GraphDatabaseEngine aggregateDatabase)
            {
                throw new NotSupportedException("Multi-relationship aggregate scans require a native snapshot.");
            }
            await foreach (var page in AggregateScanAsync(compiled, request,
                _ => throw new InvalidOperationException("Node source selected for multi-relationship aggregation."),
                budget, offset, limit, cancellation, preparedSource: (plan, evaluate, token) =>
                    ReadMultiRelationshipAggregateInputs(query, request, aggregateDatabase, plan, budget, evaluate, token)))
            {
                yield return page;
            }
            yield break;
        }
        if (database is not GraphDatabaseEngine native || compiled.OmitsResult || compiled.RequiresCompleteGraph ||
            compiled.Pipeline is not null || compiled.Union is not null || compiled.ReturnsEntityGraph ||
            query.Aggregation is not null || query.Relationships.Count > 64 || query.Relationships.Any(relationship =>
                !relationship.Hops.IsFixed || relationship.MatchKind != GraphPatternMatchKind.Required))
        {
            throw new NotSupportedException("Multi-relationship scans require a native snapshot and fixed required relationships with local projection/filter, without aggregation or other query stages.");
        }
        bool spooled = ordering.Count != 0 || query.Distinct;
        if (spooled && (budget.MaximumSortTemporaryBytes == 0 || compiled.Projection is null))
        {
            throw new NotSupportedException("Ordered or DISTINCT relationship scans require scalar projection and an enabled temporary-sort budget.");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(budget.Timeout ?? TimeSpan.FromMinutes(5));
        using var lease = await native.AcquireSnapshotAsync(request.SnapshotVersion is { } requested ? new(requested) : null, deadline.Token);
        var snapshot = await native.CaptureScanSnapshotAsync(lease, deadline.Token);
        if (limit == 0)
        {
            yield return new(lease.Version, [], true);
            yield break;
        }
        await using var sorter = spooled ? new EdgeScanSort(compiled, ordering, budget.MaximumSortTemporaryBytes) : null;
        var scanQuery = new BoundedGraphPatternQuery(query.Start, query.Relationships,
            maximumMatches: budget.PageSize, returnedBindings: query.ReturnedBindings, filter: query.Filter);
        var executor = new BoundedGraphPatternExecutor(null, capturedView: snapshot.View);
        Func<Func<string, string, QueryValue>, GraphPatternMatch>? projection = sorter is not null ? sorter.Project :
            compiled.Projection is null ? null : property => GraphGqlPropertyProjectionShaper.Project(compiled.Projection, property);
        long examined = 0, matched = 0, emitted = 0;
        void Charge()
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (++examined > budget.MaximumExaminedElements)
            {
                throw new QueryScanWorkLimitException("Graph scan exceeded its examined-element budget; the result is incomplete.");
            }
        }
        GraphPatternMatch Rename(GraphPatternMatch row) => compiled.OutputNames is { Count: > 0 } names
            ? new(row.Bindings.Select(pair => new KeyValuePair<string, QueryValue>(names.GetValueOrDefault(pair.Key, pair.Key), pair.Value)), row.Paths)
            : row;
        var rows = new List<GraphPatternMatch>();
        foreach (var input in executor.ScanFixedMatches(scanQuery, snapshot, Charge, projection, deadline.Token))
        {
            var row = Rename(input);
            if (sorter is not null)
            {
                await sorter.AddAsync(row, deadline.Token);
                continue;
            }
            if (matched++ < offset)
            {
                continue;
            }
            if (++emitted > budget.MaximumRows)
            {
                throw new QueryScanWorkLimitException("Graph scan exceeded its result-row budget; the result is incomplete.");
            }
            rows.Add(row);
            if (rows.Count == budget.PageSize || emitted == limit)
            {
                native.ValidateSnapshot(lease);
                yield return new(lease.Version, rows, emitted == limit);
                rows = [];
            }
            if (emitted == limit)
            {
                yield break;
            }
        }
        if (sorter is not null)
        {
            await foreach (var row in sorter.ReadAsync(deadline.Token))
            {
                if (emitted == limit)
                {
                    break;
                }
                if (matched++ < offset)
                {
                    continue;
                }
                if (++emitted > budget.MaximumRows)
                {
                    throw new QueryScanWorkLimitException("Graph scan exceeded its result-row budget; the result is incomplete.");
                }
                rows.Add(row);
                if (rows.Count == budget.PageSize)
                {
                    native.ValidateSnapshot(lease);
                    yield return new(lease.Version, rows, false);
                    rows = [];
                }
            }
        }
        deadline.Token.ThrowIfCancellationRequested();
        native.ValidateSnapshot(lease);
        yield return new(lease.Version, rows, true);
    }

    private static async IAsyncEnumerable<AggregateInputPage> ReadMultiRelationshipAggregateInputs(
        BoundedGraphPatternQuery query, GraphGqlQueryRequest request, GraphDatabaseEngine native,
        GraphPatternAggregation plan, GqlScanBudget budget, bool evaluateValues,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        using var lease = await native.AcquireSnapshotAsync(request.SnapshotVersion is { } requested ? new(requested) : null, cancellation);
        var snapshot = await native.CaptureScanSnapshotAsync(lease, cancellation);
        if (!evaluateValues)
        {
            yield return new(lease.Version, [], 0, true);
            yield break;
        }
        var bindings = query.Relationships.SelectMany(item => new[] { item.Binding, item.Target.Binding })
            .Prepend(query.Start.Binding).Distinct(StringComparer.Ordinal).ToArray();
        var scanQuery = new BoundedGraphPatternQuery(query.Start, query.Relationships,
            maximumMatches: budget.PageSize, returnedBindings: bindings, filter: query.Filter);
        var executor = new BoundedGraphPatternExecutor(null, capturedView: snapshot.View);
        long examined = 0, reported = 0;
        void Charge()
        {
            cancellation.ThrowIfCancellationRequested();
            if (++examined > budget.MaximumExaminedElements)
            {
                throw new QueryScanWorkLimitException("Graph aggregate scan exceeded its examined-element budget; the result is incomplete.");
            }
        }
        var rows = new List<GraphPatternMatch>();
        foreach (var match in executor.ScanFixedMatches(scanQuery, snapshot, Charge, cancellation: cancellation))
        {
            rows.Add(CaptureAggregateMatch(plan, match));
            if (rows.Count == budget.PageSize)
            {
                native.ValidateSnapshot(lease);
                yield return new(lease.Version, rows, examined - reported, false);
                reported = examined;
                rows = [];
            }
        }
        cancellation.ThrowIfCancellationRequested();
        native.ValidateSnapshot(lease);
        yield return new(lease.Version, rows, examined - reported, true);
    }
}
