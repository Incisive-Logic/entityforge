using System.Runtime.CompilerServices;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;

namespace EntityForge.Db.Gql;

internal sealed record GqlEdgeScanInputPage(DatabaseVersion Version, IReadOnlyList<GraphVertex> Vertices,
    IReadOnlyList<GraphEdge> Edges, int ExaminedElements, bool Complete);

internal static partial class GraphGqlScan
{
    internal static async IAsyncEnumerable<GqlScanPage> ExecuteNativeAsync(GraphGqlQueryRequest request,
        IGraphDatabase database, GqlScanBudget budget, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(database);
        ValidateBudget(budget);
        var window = ExtractWindow(request);
        var (compiled, ordering) = CompileScan(window.Request);
        if (compiled.Query.Relationships.Count > 1)
        {
            await foreach (var page in ExecuteMultiRelationshipScanAsync(compiled, request, ordering, database,
                budget, window.Offset, window.Limit, cancellation))
            {
                yield return page;
            }
            yield break;
        }
        if (compiled.Query.Relationships.Count == 0)
        {
            await foreach (var page in ExecuteAsync(request, token => ReadNativeAsync(database,
                request.SnapshotVersion is { } version ? new(version) : null, budget.PageSize, token), budget, cancellation))
            {
                yield return page;
            }
            yield break;
        }
        if (compiled.Query.Aggregation is not null || compiled.Pipeline?.Stages.Any(stage => stage.Aggregation is not null) == true)
        {
            await foreach (var page in AggregateScanAsync(compiled, request, _ => throw new InvalidOperationException("Node source selected for relationship aggregation."),
                budget, window.Offset, window.Limit, cancellation,
                token => ReadNativeEdgesAsync(database, request.SnapshotVersion is { } version ? new(version) : null, budget.PageSize, token)))
            {
                yield return page;
            }
            yield break;
        }
        await foreach (var page in ExecuteEdgesAsync(compiled, window.Offset, window.Limit,
            token => ReadNativeEdgesAsync(database, request.SnapshotVersion is { } version ? new(version) : null,
                budget.PageSize, token), budget, cancellation, ordering))
        {
            yield return page;
        }
    }

    // The evaluator consumes bounded edge-and-endpoint pages. A future profile
    // adapter can provide this contract without changing GQL filtering or projection.
    internal static async IAsyncEnumerable<GqlScanPage> ExecuteEdgesAsync(CompiledGraphGqlQuery compiled,
        long offset, long limit, Func<CancellationToken, IAsyncEnumerable<GqlEdgeScanInputPage>> source,
        GqlScanBudget budget, [EnumeratorCancellation] CancellationToken cancellation = default,
        IReadOnlyList<GraphPatternOrderTerm>? ordering = null)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(budget);
        var query = compiled.Query;
        ValidateBudget(budget);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        if (compiled.OmitsResult || compiled.RequiresCompleteGraph || compiled.Pipeline is not null ||
            compiled.Union is not null || compiled.ReturnsEntityGraph || query.Aggregation is not null ||
            query.Relationships.Count != 1 ||
            !query.Relationships[0].Hops.IsFixed || query.Relationships[0].MatchKind != GraphPatternMatchKind.Required)
        {
            throw new NotSupportedException("Relationship scans require one fixed required relationship and a local projection/filter, without aggregation or other query stages.");
        }
        ordering ??= query.OrderBy;
        bool spooled = ordering.Count != 0 || query.Distinct;
        if (spooled && (budget.MaximumSortTemporaryBytes == 0 || compiled.Projection is null))
        {
            throw new NotSupportedException("Ordered or DISTINCT relationship scans require scalar projection and an enabled temporary-sort budget.");
        }
        await using var sorter = spooled ? new EdgeScanSort(compiled, ordering, budget.MaximumSortTemporaryBytes) : null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(budget.Timeout ?? TimeSpan.FromMinutes(5));
        var pageQuery = new BoundedGraphPatternQuery(query.Start, query.Relationships,
            maximumMatches: budget.PageSize * 2, returnedBindings: query.ReturnedBindings, filter: query.Filter);
        DatabaseVersion? version = null;
        long examined = 0, matched = 0, emitted = 0;
        await foreach (var page in source(deadline.Token).WithCancellation(deadline.Token))
        {
            deadline.Token.ThrowIfCancellationRequested();
            version ??= page.Version;
            if (version != page.Version || compiled.SnapshotVersion is { } requested && requested != page.Version.Value)
            {
                throw new InvalidDataException("Relationship scan source violated its snapshot contract.");
            }
            ValidateEdgePage(page, budget);
            examined += page.ExaminedElements;
            if (examined > budget.MaximumExaminedElements)
            {
                throw new QueryScanWorkLimitException("Relationship scan exceeded its examined-element budget; the result is incomplete.");
            }
            if (limit == 0)
            {
                yield return new(page.Version, [], true);
                yield break;
            }
            var view = GraphReadView.Build(page.Vertices.ToDictionary(vertex => vertex.Id), page.Edges.ToDictionary(edge => edge.Id));
            var snapshot = new GraphQuerySnapshot(page.Version, view, view.StatisticsFor(page.Version));
            var executor = new BoundedGraphPatternExecutor(null, capturedView: view);
            Func<Func<string, string, EntityForge.Query.Values.QueryValue>, GraphPatternMatch>? projection = sorter is not null
                ? sorter.Project : compiled.Projection is null ? null : property => GraphGqlPropertyProjectionShaper.Project(compiled.Projection, property);
            var result = await compiled.ExecuteAsync(_ => executor.ExecuteCapturedResultAsync(pageQuery, snapshot,
                deadline.Token, projection), deadline.Token);
            var matches = ((GraphPatternResult)result.Result).Matches;
            if (sorter is not null)
            {
                foreach (var row in matches)
                {
                    await sorter.AddAsync(row, deadline.Token);
                }
                if (page.Complete)
                {
                    var sortedRows = new List<GraphPatternMatch>();
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
                            throw new QueryScanWorkLimitException("Relationship scan exceeded its result-row budget; the result is incomplete.");
                        }
                        sortedRows.Add(row);
                        if (sortedRows.Count == budget.PageSize)
                        {
                            yield return new(page.Version, sortedRows, false);
                            sortedRows = [];
                        }
                    }
                    yield return new(page.Version, sortedRows, true);
                    yield break;
                }
                continue;
            }
            int skip = (int)Math.Min(matches.Count, Math.Max(0, offset - matched));
            matched += matches.Count;
            int take = (int)Math.Min(matches.Count - skip, limit - emitted);
            emitted += take;
            if (emitted > budget.MaximumRows)
            {
                throw new QueryScanWorkLimitException("Relationship scan exceeded its result-row budget; the result is incomplete.");
            }
            bool complete = page.Complete || emitted == limit;
            var rows = matches.Skip(skip).Take(take).ToArray();
            if (rows.Length == 0)
            {
                yield return new(page.Version, [], complete);
            }
            for (int start = 0; start < rows.Length; start += budget.PageSize)
            {
                yield return new(page.Version, rows.Skip(start).Take(budget.PageSize).ToArray(),
                    complete && start + budget.PageSize >= rows.Length);
            }
            if (complete)
            {
                yield break;
            }
        }
        throw new InvalidDataException("Relationship scan source ended without a completion page.");
    }

    private static void ValidateEdgePage(GqlEdgeScanInputPage page, GqlScanBudget budget)
    {
        if (page.Edges.Count > budget.PageSize || page.Vertices.Count > page.Edges.Count * 2 ||
            page.ExaminedElements < page.Edges.Count + page.Vertices.Count || page.ExaminedElements > budget.PageSize * 3)
        {
            throw new InvalidDataException("Relationship scan source violated its bounded-page contract.");
        }
        var vertexIds = page.Vertices.Select(vertex => vertex.Id).ToHashSet();
        if (vertexIds.Count != page.Vertices.Count || page.Edges.Select(edge => edge.Id).Distinct().Count() != page.Edges.Count ||
            page.Edges.Any(edge => !vertexIds.Contains(edge.From) || !vertexIds.Contains(edge.To)))
        {
            throw new InvalidDataException("Relationship scan pages require unique elements and both endpoints of every edge.");
        }
    }

    private static async IAsyncEnumerable<GqlEdgeScanInputPage> ReadNativeEdgesAsync(IGraphDatabase database,
        DatabaseVersion? version, int pageSize, [EnumeratorCancellation] CancellationToken cancellation)
    {
        using var snapshot = await database.AcquireSnapshotAsync(version, cancellation);
        await foreach (var page in ReadPages().WithCancellation(cancellation))
        {
            var edges = page.Effects.OfType<GraphEdgeCreated>().Select(created => created.Edge).ToArray();
            var vertices = new List<GraphVertex>();
            foreach (var id in edges.SelectMany(edge => new[] { edge.From, edge.To }).Distinct())
            {
                database.ValidateSnapshot(snapshot);
                vertices.Add(await database.GetVertexAsync(id, snapshot.Version, cancellation) ??
                    throw new InvalidDataException("An edge snapshot references a missing endpoint."));
            }
            yield return new(snapshot.Version, vertices, edges, page.Effects.Count + vertices.Count, page.Complete);
        }

        async IAsyncEnumerable<(IReadOnlyList<GraphCommittedEffect> Effects, bool Complete)> ReadPages()
        {
            if (database is GraphDatabaseEngine native)
            {
                await foreach (var page in native.ReadScanPagesAsync(snapshot, pageSize, false, true, cancellation))
                {
                    yield return page;
                }
                yield break;
            }
            GraphSnapshotCursor? cursor = null;
            do
            {
                var page = await database.ReadSnapshotPageAsync(snapshot, cursor, pageSize, cancellation);
                cursor = page.NextCursor;
                yield return (page.Effects.Effects, cursor is null);
            } while (cursor is not null);
        }
    }
}
