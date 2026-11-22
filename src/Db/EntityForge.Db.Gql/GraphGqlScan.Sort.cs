using System.Runtime.CompilerServices;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Query.Planning;
using EntityForge.Query.Querying;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlScan
{
    private static async IAsyncEnumerable<GqlScanInputPage> OrderSource(
        IAsyncEnumerable<GqlScanInputPage> source, BoundedGraphPatternQuery query,
        GqlPropertyProjection? projection, bool distinct, long? requestedVersion, GqlScanBudget budget,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var comparer = Comparer<GraphScanOrderedVertex>.Create((left, right) => BoundedGraphPatternExecutor.CompareScanKeys(left, right, query));
        string Key(GraphScanOrderedVertex row) => projection is null ? row.Vertex.Id.Value.ToString("N") :
            GqlRowPipeline.RowKey(BoundedGraphPatternExecutor.ProjectScanValues(row.Vertex, query,
                property => GraphGqlPropertyProjectionShaper.Project(projection, property)).Bindings.Values);
        await using IExternalSort<GraphScanOrderedVertex> sorter = distinct ? new ExternalDistinctSort<GraphScanOrderedVertex>(
            Key, BoundedGraphPatternExecutor.EncodeScanRow, BoundedGraphPatternExecutor.DecodeScanRow,
            query.OrderBy.Count == 0 ? null : comparer, new ExternalSortDiskBudget(budget.MaximumSortTemporaryBytes)) : new ExternalMergeSort<GraphScanOrderedVertex>(
            comparer,
            BoundedGraphPatternExecutor.EncodeScanRow, BoundedGraphPatternExecutor.DecodeScanRow,
            maximumDiskBytes: budget.MaximumSortTemporaryBytes);
        DatabaseVersion? version = null;
        long examined = 0;
        bool complete = false;
        await foreach (var page in source.WithCancellation(cancellation).ConfigureAwait(false))
        {
            cancellation.ThrowIfCancellationRequested();
            version ??= page.Version;
            if (version != page.Version || requestedVersion is { } requested && requested != page.Version.Value ||
                page.ExaminedElements < page.Vertices.Count || page.ExaminedElements > budget.PageSize)
            {
                throw new InvalidDataException("GQL ordered scan source violated its snapshot or bounded-page contract.");
            }
            examined += page.ExaminedElements;
            if (examined > budget.MaximumExaminedElements)
            {
                throw new QueryScanWorkLimitException("GQL ordered scan exceeded its examined-element budget.");
            }
            foreach (var vertex in page.Vertices)
            {
                cancellation.ThrowIfCancellationRequested();
                if (BoundedGraphPatternExecutor.MatchesScanVertex(vertex, query))
                {
                    await sorter.AddAsync(BoundedGraphPatternExecutor.CaptureScanKeys(vertex, query), cancellation).ConfigureAwait(false);
                }
            }
            if (page.Complete)
            {
                complete = true;
                break;
            }
        }
        if (!complete)
        {
            throw new InvalidDataException("GQL ordered scan source ended without completion.");
        }
        var vertices = new List<GraphVertex>();
        await foreach (var row in sorter.ReadAsync(cancellation).ConfigureAwait(false))
        {
            vertices.Add(row.Vertex);
            if (vertices.Count == budget.PageSize)
            {
                yield return new(version!.Value, vertices, vertices.Count, false);
                vertices = [];
            }
        }
        yield return new(version!.Value, vertices, vertices.Count, true);
    }
}
