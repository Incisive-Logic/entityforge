using EntityForge.Query.Results;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlScan
{
    // Row-local stages may run once per completed group. DISTINCT and ORDER BY
    // stay global; moving an earlier DISTINCT past another projection loses bags.
    private sealed record AggregateScanProjection(GqlRowPipeline? Pipeline, string[] Aliases, bool Distinct)
    {
        internal GraphPatternMatch? Apply(GraphPatternMatch row, CancellationToken cancellation)
        {
            if (Pipeline is null)
            {
                return row;
            }
            var input = new QueryResultEnvelope(Guid.NewGuid(), new GraphPatternResult(new GraphResult([], []), [row]));
            return ((GraphPatternResult)Pipeline.Apply(input, cancellation).Result).Matches.SingleOrDefault();
        }
    }

    private static AggregateScanProjection PlanAggregateProjection(IReadOnlyList<GqlRowStage>? stages, string[] aliases)
    {
        if (stages is null)
        {
            return new(null, aliases, false);
        }
        var suffix = stages.Skip(1).ToArray();
        int ordering = Array.FindIndex(suffix, stage => stage.Operation == "ORDER BY");
        if (ordering >= 0 && ordering != suffix.Length - 1)
        {
            throw new NotSupportedException("Aggregate scans require ordering after their row-local stages.");
        }
        var transforms = suffix.Where(stage => stage.Operation != "ORDER BY").ToArray();
        if (transforms.Any(stage => stage.Operation is not ("PROJECT" or "FILTER" or "WHERE") ||
            stage.Aggregation is not null || stage.PreserveImports || stage.ValueSubqueries?.Count > 0) ||
            transforms.Take(Math.Max(0, transforms.Length - 1)).Any(stage => stage.Distinct))
        {
            throw new NotSupportedException("Aggregate scans require local projections/filters and at most a final result DISTINCT.");
        }
        var names = aliases.ToList();
        bool distinct = stages[0].Distinct;
        foreach (var stage in transforms)
        {
            if (stage.Operation == "PROJECT")
            {
                names = stage.Terms!.Select(term => term.Alias).ToList();
                distinct = stage.Distinct;
            }
            if (stage.InputValueAlias is { } consumed)
            {
                names.Remove(consumed);
            }
            if (stage.DiscardBindings is { } discarded)
            {
                names.RemoveAll(discarded.Contains);
            }
        }
        var finalDiscard = ordering < 0 ? [] : suffix[ordering].DiscardBindings ?? [];
        names.RemoveAll(finalDiscard.Contains);
        return new(transforms.Length == 0 ? null : new(transforms.Select(stage => stage with { Distinct = false }).ToArray()),
            names.ToArray(), distinct);
    }
}
