using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Query.Planning;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static async ValueTask<List<Dictionary<string, RdfTerm>>> CompleteServiceRowsAsync(
        RdfSparqlReadView database, List<Dictionary<string, RdfTerm>> rows, SelectProjection projection,
        Modifiers modifiers, ServiceExecution execution)
    {
        var aggregates = projection.Aggregates.ToArray();
        for (int index = 0; index < aggregates.Length; index++)
        {
            var aggregate = aggregates[index];
            if (aggregate.Value is null || !Services(aggregate.Value).Any())
            {
                continue;
            }
            // Evaluate each input exactly once, before grouping. Reserved names
            // cannot collide with user variables or leak through projection.
            string name = "$serviceAggregate" + index;
            rows = await BindServiceRowsAsync(database, new(aggregate.Value, name), rows, execution).ConfigureAwait(false);
            aggregates[index] = aggregate with
            {
                Value = new TermValueExpression(new(name, null, null))
            };
        }
        rows = ApplyAggregation(rows, projection with
        {
            Aggregates = aggregates
        }, modifiers.GroupVariables, database);
        if (modifiers.Having is { } having)
        {
            rows = await FilterServiceRowsAsync(database, rows, [having], execution, projection).ConfigureAwait(false);
        }
        if (modifiers.TrailingValues is { } values)
        {
            rows = EvaluateValues(values, rows, database.Cancellation);
        }
        foreach (var computed in projection.Computed)
        {
            rows = await BindServiceRowsAsync(database, computed, rows, execution).ConfigureAwait(false);
        }
        if (modifiers.Order.Count > 0)
        {
            var ordered = new List<OrderedSolution>(rows.Count);
            foreach (var row in rows)
            {
                var keys = new RdfTerm?[modifiers.Order.Count];
                for (int index = 0; index < keys.Length; index++)
                {
                    keys[index] = await EvaluateServiceValueAsync(database, modifiers.Order[index].Value, row, execution).ConfigureAwait(false);
                }
                ordered.Add(new(row, CanonicalTieKey(row), keys));
            }
            QueryComparisonSort.Sort(ordered, (left, right) => CompareOrdered(left, right, modifiers), database.Cancellation);
            rows = ordered.Select(item => item.Row).ToList();
        }
        return rows;
    }

    private static async ValueTask<IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>>> CompleteServiceSelectAsync(
        RdfSparqlReadView database, List<Dictionary<string, RdfTerm>> rows, ParsedSelectQuery query, ServiceExecution execution)
    {
        rows = await CompleteServiceRowsAsync(database, rows, query.Projection, query.Modifiers, execution).ConfigureAwait(false);
        IEnumerable<IReadOnlyDictionary<string, RdfTerm>> projected = rows.Select(row => ProjectRow(row, query.Projection));
        if (query.Distinct)
        {
            projected = DistinctRows(projected);
        }
        return projected.Skip(query.Modifiers.Offset).Take(query.Modifiers.Limit).ToArray();
    }
}
