using System.Runtime.CompilerServices;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;

namespace EntityForge.Db.Gql;

internal sealed record GqlScanBudget(int MaximumRows = 1_000_000, int MaximumExaminedElements = 10_000_000,
    int PageSize = 256, TimeSpan? Timeout = null, int MaximumSortTemporaryBytes = 0);

internal sealed record GqlScanInputPage(DatabaseVersion Version, IReadOnlyList<GraphVertex> Vertices,
    int ExaminedElements, bool Complete);

internal sealed record GqlScanPage(DatabaseVersion Version, IReadOnlyList<GraphPatternMatch> Rows, bool Complete,
    IReadOnlyList<QueryResultDiagnostic>? Diagnostics = null);

internal sealed class QueryScanWorkLimitException(string message) : Exception(message);

internal static partial class GraphGqlScan
{
    private static void ValidateBudget(GqlScanBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.MaximumRows < 1 || budget.MaximumExaminedElements < 1 || budget.PageSize is < 1 or > 1000 ||
            budget.MaximumSortTemporaryBytes < 0 ||
            budget.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1)))
        {
            throw new ArgumentOutOfRangeException(nameof(budget));
        }
    }

    // A future semantic graph adapter can supply pages without reimplementing GQL syntax,
    // value evaluation, pattern filtering, or projection. Cross-page operators need their own plans.
    internal static async IAsyncEnumerable<GqlScanPage> ExecuteAsync(GraphGqlQueryRequest request,
        Func<CancellationToken, IAsyncEnumerable<GqlScanInputPage>> source, GqlScanBudget budget,
        [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(budget);
        ValidateBudget(budget);
        var window = ExtractWindow(request);
        var (compiled, ordering) = CompileScan(window.Request);
        if (compiled.Query.Aggregation is not null || compiled.Pipeline?.Stages.Any(stage => stage.Aggregation is not null) == true)
        {
            await foreach (var page in AggregateScanAsync(compiled, request, source, budget, window.Offset, window.Limit, cancellation).ConfigureAwait(false))
            {
                yield return page;
            }
            yield break;
        }
        var query = compiled.Query;
        if (compiled.OmitsResult || compiled.RequiresCompleteGraph || compiled.Pipeline is not null ||
            compiled.Union is not null || compiled.ReturnsEntityGraph || query.Relationships.Count != 0 ||
            query.Aggregation is not null)
        {
            throw new NotSupportedException("GQL scan execution requires a single-node MATCH and projection, with optional ordering and LIMIT/OFFSET but without aggregation or other row operators. Other query shapes require a different execution plan.");
        }
        bool ordered = ordering.Count != 0;
        bool spooled = ordered || query.Distinct;
        if (spooled && budget.MaximumSortTemporaryBytes == 0)
        {
            throw new NotSupportedException("Ordered or DISTINCT GQL scans require an enabled temporary-sort budget.");
        }
        var pageQuery = new BoundedGraphPatternQuery(query.Start, maximumMatches: budget.PageSize,
            returnedBindings: query.ReturnedBindings, filter: query.Filter, orderBy: ordering);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(budget.Timeout ?? TimeSpan.FromMinutes(5));
        DatabaseVersion? version = null;
        long examined = 0, emitted = 0, matched = 0;
        var pages = source(deadline.Token);
        if (spooled && window.Limit != 0)
        {
            pages = OrderSource(pages, pageQuery, compiled.Projection, query.Distinct, request.SnapshotVersion, budget, deadline.Token);
        }
        await foreach (var page in pages.WithCancellation(deadline.Token).ConfigureAwait(false))
        {
            deadline.Token.ThrowIfCancellationRequested();
            version ??= page.Version;
            if (version != page.Version || request.SnapshotVersion is { } requested && requested != page.Version.Value ||
                page.ExaminedElements < page.Vertices.Count || page.ExaminedElements > budget.PageSize)
            {
                throw new InvalidDataException("GQL scan source violated its snapshot or bounded-page contract.");
            }
            if (window.Limit == 0)
            {
                yield return new(page.Version, [], true);
                yield break;
            }
            examined += page.ExaminedElements;
            if (examined > budget.MaximumExaminedElements)
            {
                throw new QueryScanWorkLimitException("GQL scan exceeded its examined-element budget; the result is incomplete.");
            }
            var view = GraphReadView.Build(page.Vertices.ToDictionary(vertex => vertex.Id), new Dictionary<GraphEdgeId, GraphEdge>());
            var snapshot = new GraphQuerySnapshot(page.Version, view, view.StatisticsFor(page.Version));
            var executor = new BoundedGraphPatternExecutor(null, capturedView: view);
            Func<Func<string, string, EntityForge.Query.Values.QueryValue>, GraphPatternMatch>? projection =
                compiled.Projection is null ? null : property => GraphGqlPropertyProjectionShaper.Project(compiled.Projection, property);
            var result = await compiled.ExecuteAsync(_ => spooled
                ? ValueTask.FromResult(executor.ProjectOrderedScanPage(pageQuery, page.Vertices, page.Version, projection))
                : executor.ExecuteCapturedResultAsync(pageQuery, snapshot, deadline.Token, projection),
                deadline.Token).ConfigureAwait(false);
            var matches = ((GraphPatternResult)result.Result).Matches;
            int skip = (int)Math.Min(matches.Count, Math.Max(0, window.Offset - matched));
            matched += matches.Count;
            int take = (int)Math.Min(matches.Count - skip, window.Limit - emitted);
            var rows = matches.Skip(skip).Take(take).ToArray();
            emitted += rows.Length;
            if (emitted > budget.MaximumRows)
            {
                throw new QueryScanWorkLimitException("GQL scan exceeded its result-row budget; the result is incomplete.");
            }
            bool complete = page.Complete || emitted == window.Limit;
            yield return new(page.Version, rows, complete);
            if (complete)
            {
                yield break;
            }
        }
        throw new InvalidDataException("GQL scan source ended without a completion page.");
    }

    internal static async IAsyncEnumerable<GqlScanInputPage> ReadNativeAsync(IGraphDatabase database,
        DatabaseVersion? version, int pageSize, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        using var snapshot = await database.AcquireSnapshotAsync(version, cancellation).ConfigureAwait(false);
        if (database is GraphDatabaseEngine native)
        {
            await foreach (var page in native.ReadScanPagesAsync(snapshot, pageSize, true, false, cancellation).ConfigureAwait(false))
            {
                yield return new(snapshot.Version, page.Effects.OfType<GraphVertexCreated>().Select(created => created.Vertex).ToArray(),
                    page.Effects.Count, page.Complete);
            }
            yield break;
        }
        GraphSnapshotCursor? cursor = null;
        do
        {
            var page = await database.ReadSnapshotPageAsync(snapshot, cursor, pageSize, cancellation).ConfigureAwait(false);
            cursor = page.NextCursor;
            yield return new(snapshot.Version, page.Effects.Effects.OfType<GraphVertexCreated>().Select(created => created.Vertex).ToArray(),
                page.Effects.Effects.Count, cursor is null);
        } while (cursor is not null);
    }
}
