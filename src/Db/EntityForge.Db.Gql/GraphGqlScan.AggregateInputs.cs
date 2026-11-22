using System.Runtime.CompilerServices;
using EntityForge.Db.Graph;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlScan
{
    private static readonly LiteralValue CountContribution = new("1", QueryScalarKind.Integer);

    private sealed record AggregateInputPage(DatabaseVersion Version, IReadOnlyList<GraphPatternMatch> Rows,
        long ExaminedElements, bool Complete);

    private static GraphPatternMatch CaptureAggregateValues(GraphPatternAggregation plan,
        Func<string, string, QueryValue> property, Func<string, QueryValue> identity)
    {
        QueryValue Value(GraphScalarExpression expression) => expression.Binding == "__row"
            ? identity(expression.Property!) : GraphScalarEvaluator.Evaluate(expression,
            (binding, name) => property(binding.StartsWith("__entity_", StringComparison.Ordinal) ? binding[9..] : binding, name));
        return new(plan.GroupBy.Select(key => new KeyValuePair<string, QueryValue>(key.Alias, Value(key.Expression!)))
            .Concat(plan.Aggregates.Select(item => new KeyValuePair<string, QueryValue>(item.Alias,
                item.Expression is { } expression ? Value(expression) : item.ValueProperty is { } name
                    ? property(item.ValueBinding!, name) : item.ValueBinding is { } binding
                        ? identity(binding) : CountContribution))), []);
    }

    private static GraphPatternMatch CaptureAggregateMatch(GraphPatternAggregation plan, GraphPatternMatch match)
    {
        QueryValue Identity(string binding) => match.Bindings.GetValueOrDefault(binding) switch
        {
            NodeValue node => new LiteralValue(GraphScalarEvaluator.DistinctKey(node)),
            EdgeValue edge => new LiteralValue(GraphScalarEvaluator.DistinctKey(edge)),
            _ => UnboundValue.Instance
        };
        return CaptureAggregateValues(plan,
            (binding, name) => GraphGqlPropertyProjectionShaper.Evaluate(match, new(binding, name, null)), Identity);
    }

    private static async IAsyncEnumerable<AggregateInputPage> ReadAggregateInputs(BoundedGraphPatternQuery query,
        GraphPatternAggregation plan, Func<CancellationToken, IAsyncEnumerable<GqlScanInputPage>> nodeSource,
        Func<CancellationToken, IAsyncEnumerable<GqlEdgeScanInputPage>>? edgeSource, GqlScanBudget budget, bool evaluateValues,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        if (edgeSource is null)
        {
            await foreach (var page in nodeSource(cancellation).WithCancellation(cancellation).ConfigureAwait(false))
            {
                if (page.ExaminedElements < page.Vertices.Count || page.ExaminedElements > budget.PageSize)
                {
                    throw new InvalidDataException("GQL aggregate source violated its bounded-page contract.");
                }
                var rows = new List<GraphPatternMatch>();
                foreach (var vertex in evaluateValues ? page.Vertices : [])
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (BoundedGraphPatternExecutor.MatchesScanVertex(vertex, query))
                    {
                        rows.Add(BoundedGraphPatternExecutor.ProjectScanValues(vertex, query,
                            property => CaptureAggregateValues(plan, property, _ => new LiteralValue(vertex.Id.Value.ToString("D")))));
                    }
                }
                yield return new(page.Version, rows, page.ExaminedElements, page.Complete);
            }
            yield break;
        }
        var bindings = new[] { query.Start.Binding, query.Relationships[0].Binding, query.Relationships[0].Target.Binding }
            .Distinct(StringComparer.Ordinal).ToArray();
        var pageQuery = new BoundedGraphPatternQuery(query.Start, query.Relationships,
            maximumMatches: budget.PageSize * 2, returnedBindings: bindings, filter: query.Filter);
        await foreach (var page in edgeSource(cancellation).WithCancellation(cancellation).ConfigureAwait(false))
        {
            cancellation.ThrowIfCancellationRequested();
            ValidateEdgePage(page, budget);
            if (!evaluateValues)
            {
                yield return new(page.Version, [], page.ExaminedElements, page.Complete);
                continue;
            }
            var view = GraphReadView.Build(page.Vertices.ToDictionary(vertex => vertex.Id), page.Edges.ToDictionary(edge => edge.Id));
            var snapshot = new GraphQuerySnapshot(page.Version, view, view.StatisticsFor(page.Version));
            var executor = new BoundedGraphPatternExecutor(null, capturedView: view);
            var result = await executor.ExecuteCapturedResultAsync(pageQuery, snapshot, cancellation).ConfigureAwait(false);
            var rows = new List<GraphPatternMatch>();
            foreach (var match in ((GraphPatternResult)result.Result).Matches)
            {
                cancellation.ThrowIfCancellationRequested();
                rows.Add(CaptureAggregateMatch(plan, match));
            }
            yield return new(page.Version, rows, page.ExaminedElements, page.Complete);
        }
    }
}
