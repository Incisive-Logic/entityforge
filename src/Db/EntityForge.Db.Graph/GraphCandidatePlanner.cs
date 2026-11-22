using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Query.Planning;

namespace EntityForge.Db.Graph;

internal sealed record GraphCandidateQuery(
    IReadOnlyList<GraphVertexId>? Identities = null,
    string? Property = null,
    DatabaseValue? Value = null,
    IReadOnlyList<string>? Labels = null,
    IReadOnlyList<GraphNumericBound>? NumericBounds = null,
    GraphScalarNumericEquality? ScalarNumericEquality = null,
    IReadOnlyList<GraphScalarNumericBound>? ScalarNumericBounds = null,
    GraphScalarStringEquality? ScalarStringEquality = null);

internal sealed record GraphScalarStringEquality(string Property, string Value);

internal sealed record GraphScalarNumericEquality(string Property, EntityForge.Query.Values.LiteralValue Value);

internal sealed record GraphScalarNumericBound(string Property, EntityForge.Query.Values.LiteralValue Value, string Operator);

internal sealed record GraphCandidatePlan(
    IReadOnlyList<GraphVertexId> Candidates,
    IReadOnlyList<string> AccessPaths,
    IReadOnlyList<long?> EstimatedCandidateCounts,
    IReadOnlyList<QueryEstimate> Estimates,
    IReadOnlyList<long> AcquiredCandidateCounts);

internal sealed record GraphCandidatePlanDescription(
    DatabaseVersion? StatisticsVersion,
    IReadOnlyList<string> AccessPaths,
    IReadOnlyList<long?> EstimatedCandidateCounts,
    IReadOnlyList<QueryEstimate> Estimates);

internal abstract record GraphCandidatePredicate;

internal sealed record GraphIdentityCandidatePredicate(IReadOnlyList<GraphVertexId> Identities)
    : GraphCandidatePredicate;

internal sealed record GraphPropertyCandidatePredicate(string Property, DatabaseValue Value)
    : GraphCandidatePredicate;

internal sealed record GraphLabelCandidatePredicate(string Label) : GraphCandidatePredicate;

internal sealed record GraphNumericCandidatePredicate(IReadOnlyList<GraphNumericBound> Bounds) : GraphCandidatePredicate;

internal sealed record GraphAllCandidatePredicate : GraphCandidatePredicate;

internal sealed class GraphCandidatePlanner(GraphProfileRuntime runtime) :
    IEntityCandidateSource<GraphCandidateQuery, GraphCandidatePredicate, GraphVertexId>
{
    private const string IdentityAccessPath = "ordered.graph-vertex-identity";
    private const string PropertyAccessPath = "snapshot.graph-vertex-property-index";
    private const string LabelAccessPath = "snapshot.graph-vertex-label-index";
    private const string FullScanAccessPath = "snapshot.graph-vertex-scan";
    private readonly EntityCandidatePlanner<GraphCandidateQuery, GraphCandidatePredicate, GraphVertexId> _planner = new();

    public async ValueTask<GraphCandidatePlan> ExecuteAsync(
        GraphCandidateQuery query, CancellationToken cancellationToken = default)
    {
        EntityCandidatePlanResult<GraphVertexId> result = await _planner.ExecuteAsync(
            query, this, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new(result.Candidates, result.AccessPaths, result.EstimatedCandidateCounts, result.Estimates,
            result.AcquiredCandidateCounts);
    }

    public GraphCandidatePlanDescription Describe(GraphCandidateQuery query)
    {
        EntityCandidatePlanDescription plan = _planner.DescribePlan(query, this);
        return new(runtime.QueryStatistics.Version, plan.AccessPaths, plan.EstimatedCandidateCounts, plan.Estimates);
    }

    public IReadOnlyList<EntityCandidateAccess<GraphCandidatePredicate>> Select(GraphCandidateQuery query)
    {
        var selected = new List<EntityCandidateAccess<GraphCandidatePredicate>>(2);
        GraphQueryStatistics statistics = runtime.QueryStatistics;
        if (query.Identities is { Count: > 0 })
        {
            selected.Add(new(IdentityAccessPath, new GraphIdentityCandidatePredicate(query.Identities), Priority: 10,
                EstimatedCandidates: Math.Min(query.Identities.Distinct().LongCount(), statistics.VertexCount),
                EstimateQuality: QueryEstimateQuality.UpperBound, EstimateSource: "graph.identity-cardinality"));
        }

        if (query.Property is not null && query.Value is not null)
        {
            long? exact = statistics.EstimateVertexValue(query.Property, query.Value);
            long presence = statistics.VertexPropertyBound(query.Property);
            var estimate = QueryEstimatePolicy.Frequency(exact, presence, true, true,
                "graph.value-frequency", "graph.property-presence");
            selected.Add(new(PropertyAccessPath, new GraphPropertyCandidatePredicate(query.Property, query.Value),
                Priority: 50, EstimatedCandidates: estimate.Value,
                EstimateQuality: estimate.Quality, EstimateSource: estimate.Source,
                EstimatedAcquisitionWork: exact ?? 1));
        }
        if (query.Labels is { Count: > 0 })
        {
            foreach (string label in query.Labels.Distinct(StringComparer.Ordinal))
            {
                selected.Add(new(LabelAccessPath, new GraphLabelCandidatePredicate(label), Priority: 30,
                    EstimatedCandidates: statistics.VertexLabelBound(label),
                    EstimateQuality: statistics.HasExactLabelCount(label) ? QueryEstimateQuality.Exact : QueryEstimateQuality.UpperBound,
                    EstimateSource: "graph.label-cardinality"));
            }
        }

        if (query.NumericBounds is { Count: > 0 } bounds)
        {
            selected.Add(new("snapshot.graph-numeric-range", new GraphNumericCandidatePredicate(bounds),
                Priority: 40, EstimatedCandidates: statistics.VertexPropertyBound(bounds[0].Property),
                EstimateQuality: QueryEstimateQuality.UpperBound, EstimateSource: "graph.property-presence"));
        }

        return selected;
    }

    public EntityCandidateAccess<GraphCandidatePredicate> Fallback(GraphCandidateQuery query) =>
        new(FullScanAccessPath, new GraphAllCandidatePredicate(),
            EstimatedCandidates: runtime.QueryStatistics.VertexCount,
            EstimateQuality: QueryEstimateQuality.Exact, EstimateSource: "graph.vertex-cardinality");

    public ValueTask<IReadOnlyList<GraphVertexId>> AcquireAsync(
        GraphCandidateQuery query, EntityCandidateAccess<GraphCandidatePredicate> access,
        IReadOnlySet<GraphVertexId>? constraint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (constraint is null)
        {
            return ValueTask.FromResult(runtime.FindCandidates(access.Predicate));
        }

        GraphReadView view = runtime.CaptureReadView(runtime.CurrentVersion);
        IReadOnlyList<GraphVertexId> found = access.Predicate switch
        {
            GraphIdentityCandidatePredicate identity => identity.Identities
                .Where(constraint.Contains).Distinct().ToArray(),
            GraphPropertyCandidatePredicate property => constraint.Where(id =>
                view.GetVertex(id)?.Properties.TryGetValue(property.Property, out DatabaseValue? value) == true &&
                value == property.Value).ToArray(),
            GraphLabelCandidatePredicate label => constraint.Where(id =>
                view.GetVertex(id)?.Labels.Contains(label.Label) == true).ToArray(),
            GraphNumericCandidatePredicate numeric => (view.FindNumericCandidates(numeric.Bounds, cancellationToken) ??
                view.Vertices.Keys.ToArray()).Where(constraint.Contains).ToArray(),
            GraphAllCandidatePredicate => constraint.ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(access))
        };
        return ValueTask.FromResult(found);
    }
}
