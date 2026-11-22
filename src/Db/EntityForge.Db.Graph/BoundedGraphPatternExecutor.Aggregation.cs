using System.Diagnostics;
using System.Globalization;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Query.Planning;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal sealed partial class BoundedGraphPatternExecutor
{
    internal static readonly QueryResultDiagnostic NullEliminatedDiagnostic = new(
        "01G11", "Null value eliminated in set function.", QueryDiagnosticSeverity.Warning);

    // Physical plan for the existing count operators; never a second public
    // query contract. General grouping is lowered here only when equivalent.
    private sealed record SingleGraphAggregation(GraphPatternAggregateFunction Function, string? GroupBinding,
        string? ValueBinding, string? ValueProperty, string Alias, bool Distinct,
        GraphPatternSortDirection Direction, int Limit, int Offset, int MaximumInputRows);

    private static SingleGraphAggregation? SingleAggregation(GraphPatternAggregation? plan)
    {
        if (plan is null || plan.Aggregates.Count != 1 || plan.GroupBy.Count > 1 || plan.OrderBy.Count > 1)
        {
            return null;
        }
        var value = plan.Aggregates[0];
        var group = plan.GroupBy.SingleOrDefault();
        var order = plan.OrderBy.SingleOrDefault();
        if (value.Function != GraphPatternAggregateFunction.Count || value.Expression is not null ||
            value.Distinct && value.ValueProperty is not null ||
            group is not null && (group.Binding is null || group.Alias != group.Binding) ||
            order is not null && order.Alias != value.Alias)
        {
            return null;
        }
        return new(value.Function, group?.Binding, value.ValueBinding, value.ValueProperty, value.Alias, value.Distinct,
            order?.Direction ?? GraphPatternSortDirection.Ascending, plan.Limit, plan.Offset, plan.MaximumInputRows);
    }

    private BoundedGraphPatternExecution Aggregate(GraphPatternAggregation plan,
        IReadOnlyList<MatchState> states, DatabaseVersion snapshot, int candidateCount,
        TimeSpan candidateDuration, IReadOnlyList<GraphPatternRelationshipExecution> relationshipAnalyses,
        int expansionCount, int rowsBeforeLimit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (states.Count > plan.MaximumInputRows)
        {
            throw new GraphPatternExpansionLimitException(plan.MaximumInputRows);
        }
        long started = Stopwatch.GetTimestamp();
        string GeneralKey(MatchState state) => String.Concat(plan.GroupBy.Select(key =>
        {
            string value = key.Binding is { } binding ? BindingKey(state, binding) :
                GraphScalarEvaluator.DistinctKey(GraphScalarEvaluator.Evaluate(key.Expression!, state.PropertyValue));
            return value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;
        }));
        var keySelector = GroupingKeySelector(plan) ?? GeneralKey;
        var groups = QueryCancellation.Observe(states, cancellationToken).GroupBy(keySelector, StringComparer.Ordinal)
            .Select(group => (group.Key, Rows: group.ToArray())).ToList();
        if (groups.Count == 0 && plan.GroupBy.Count == 0)
        {
            groups.Add((String.Empty, []));
        }
        var evidence = new List<EvidenceRecord>();
        bool nullEliminated = false;
        var rows = new List<(string Key, Dictionary<string, QueryValue> Values)>();
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new Dictionary<string, QueryValue>(StringComparer.Ordinal);
            foreach (var key in plan.GroupBy)
            {
                var first = group.Rows[0];
                values.Add(key.Alias, key.Binding is { } binding
                    ? first.VertexOrDefault(binding) is { } vertex ? AggregateNode(vertex, snapshot, evidence) : UnboundValue.Instance
                    : GraphScalarEvaluator.Evaluate(key.Expression!, first.PropertyValue));
            }
            foreach (var aggregate in plan.Aggregates)
            {
                values.Add(aggregate.Alias, AggregateValue(aggregate, group.Rows, () => nullEliminated = true, cancellationToken));
            }
            rows.Add((group.Key, values));
        }
        return CompleteAggregation(plan, rows, evidence, nullEliminated, snapshot, candidateCount, candidateDuration,
            relationshipAnalyses, expansionCount, rowsBeforeLimit, started, cancellationToken);
    }

    private static BoundedGraphPatternExecution CompleteAggregation(GraphPatternAggregation plan,
        List<(string Key, Dictionary<string, QueryValue> Values)> rows, List<EvidenceRecord> evidence,
        bool nullEliminated, DatabaseVersion snapshot, int candidateCount, TimeSpan candidateDuration,
        IReadOnlyList<GraphPatternRelationshipExecution> relationshipAnalyses, int expansionCount, int rowsBeforeLimit,
        long started, CancellationToken cancellationToken)
    {
        IReadOnlyList<GraphPatternAggregateOrder> order = plan.OrderBy.Count == 0
            ? [new(plan.Aggregates[0].Alias)] : plan.OrderBy;
        int Compare((string Key, Dictionary<string, QueryValue> Values) left, (string Key, Dictionary<string, QueryValue> Values) right)
        {
            foreach (var term in order)
            {
                int comparison = CompareAggregateValues(left.Values[term.Alias], right.Values[term.Alias]);
                if (comparison != 0)
                {
                    return term.Direction == GraphPatternSortDirection.Descending ? -comparison : comparison;
                }
            }
            return StringComparer.Ordinal.Compare(left.Key, right.Key);
        }
        string[] aliases = plan.GroupBy.Select(key => key.Alias).Concat(plan.Aggregates.Select(value => value.Alias)).ToArray();
        var selected = SelectOrdered(rows, plan.Offset, plan.Limit, Compare, cancellationToken);
        var resultRows = selected.Select(row => new RecordRow(aliases.Select(alias => new KeyValuePair<string, QueryValue>(alias, row.Values[alias])).ToArray())).ToArray();
        var envelope = new QueryResultEnvelope(Guid.NewGuid(), new RecordResult(aliases.Select(alias => new RecordField(alias)).ToArray(), resultRows),
            evidence: evidence, diagnostics: new[] { new QueryResultDiagnostic("GRAPH_SNAPSHOT", $"Aggregated at graph snapshot {snapshot.Value}.", QueryDiagnosticSeverity.Information) }
                .Concat(nullEliminated ? [NullEliminatedDiagnostic] : []));
        return new(envelope, candidateCount, candidateDuration, relationshipAnalyses, expansionCount,
            rowsBeforeLimit, null, Stopwatch.GetElapsedTime(started));
    }

    private static Func<MatchState, string>? GroupingKeySelector(GraphPatternAggregation plan)
    {
        if (plan.GroupBy is not [{ Expression: { Binding: { } binding, Property: { } property } }])
        {
            return null;
        }
        var selector = CanonicalGroupKeySelector();
        return state => selector(state.DatabaseProperty(binding, property));
    }

    private static Func<DatabaseValue?, string> CanonicalGroupKeySelector()
    {
        // Reuse canonical keys only for repeated small scalar values within
        // this aggregation. Canonical numeric equality remains authoritative:
        // distinct database types can still belong to the same GQL group.
        var keys = new Dictionary<DatabaseValue, string>();
        string? missing = null;
        return value =>
        {
            if (value is null && missing is not null)
            {
                return missing;
            }
            bool reusable = value is EntityForge.Db.DatabaseValue.Int64 or EntityForge.Db.DatabaseValue.Decimal or
                EntityForge.Db.DatabaseValue.Double or EntityForge.Db.DatabaseValue.Boolean ||
                value is EntityForge.Db.DatabaseValue.String { Value.Length: <= 256 };
            if (reusable && keys.TryGetValue(value!, out string? existing))
            {
                return existing;
            }
            string canonical = GraphScalarEvaluator.DistinctKey(value is null ? UnboundValue.Instance : QueryValue(value));
            string key = canonical.Length.ToString(CultureInfo.InvariantCulture) + ":" + canonical;
            if (value is null)
            {
                missing = key;
            }
            else if (reusable && keys.Count < 256)
            {
                keys.Add(value, key);
            }
            return key;
        };
    }

    private static QueryValue AggregateValue(GraphPatternAggregate aggregate, IReadOnlyList<MatchState> rows,
        Action nullEliminated, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (aggregate.Expression is null && aggregate.ValueProperty is null && aggregate.ValueBinding is null)
        {
            int count = aggregate.Distinct ? Math.Min(1, rows.Count) : rows.Count;
            return new LiteralValue(count.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
        }
        var observed = QueryCancellation.Observe(rows, cancellationToken);
        if (aggregate.Expression is null && aggregate.ValueProperty is null)
        {
            IEnumerable<string> identities = aggregate.ValueBinding is null ? observed.Select(_ => "*") :
                observed.Select(row => BindingKey(row, aggregate.ValueBinding)).Where(key =>
                {
                    if (key != "u:")
                    {
                        return true;
                    }
                    nullEliminated();
                    return false;
                });
            return new LiteralValue((aggregate.Distinct ? identities.Distinct(StringComparer.Ordinal).LongCount() : identities.LongCount())
                .ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
        }
        string? binding = aggregate.Expression?.Binding ?? (aggregate.Expression is null ? aggregate.ValueBinding : null);
        string? property = aggregate.Expression?.Property ?? (aggregate.Expression is null ? aggregate.ValueProperty : null);
        if (!aggregate.Distinct && binding is not null && property is not null &&
            aggregate.Function is GraphPatternAggregateFunction.Sum or GraphPatternAggregateFunction.Average)
        {
            var accumulator = new StreamingAggregate(aggregate.Function);
            foreach (var row in observed)
            {
                var value = row.DatabaseProperty(binding, property);
                if (value is EntityForge.Db.DatabaseValue.Int64 integer)
                {
                    accumulator.AddInteger(integer.Value);
                }
                else
                {
                    accumulator.Add(value is null ? UnboundValue.Instance : QueryValue(value));
                }
            }
            if (accumulator.NullEliminated)
            {
                nullEliminated();
            }
            return accumulator.Finish();
        }
        return AggregateValues(aggregate, observed.Select(row => aggregate.Expression is { } expression
            ? GraphScalarEvaluator.Evaluate(expression, row.PropertyValue)
            : row.PropertyValue(aggregate.ValueBinding!, aggregate.ValueProperty!)), cancellationToken, nullEliminated);
    }

    internal static QueryValue AggregateValues(GraphPatternAggregate aggregate, IEnumerable<QueryValue> input,
        CancellationToken cancellationToken, Action? nullEliminated = null)
    {
        IEnumerable<QueryValue> values = QueryCancellation.Observe(input, cancellationToken).Where(value =>
        {
            if (value is not (UnboundValue or NullValue))
            {
                return true;
            }
            nullEliminated?.Invoke();
            return false;
        });
        if (aggregate.Distinct)
        {
            values = values.DistinctBy(GraphScalarEvaluator.DistinctKey);
        }
        if (StreamingAggregate.Supports(aggregate.Function))
        {
            var accumulator = new StreamingAggregate(aggregate.Function);
            foreach (var value in values)
            {
                accumulator.Add(value);
            }
            return accumulator.Finish();
        }
        if (aggregate.Function == GraphPatternAggregateFunction.Collect)
        {
            return GraphScalarEvaluator.BoundedList(values);
        }
        QueryValue[] numericValues = values.ToArray();
        if (numericValues.Any(value => value is LiteralValue { ScalarKind: QueryScalarKind.Duration }))
        {
            throw new ArgumentException("Duration aggregates require homogeneous durations and SUM or AVG.");
        }
        if (numericValues.Length > 0 && numericValues.All(value => value is LiteralValue { ScalarKind: QueryScalarKind.Integer }))
        {
            return AggregateFloatingValues(aggregate, numericValues, cancellationToken);
        }
        if (numericValues.Any(value => value is LiteralValue { ScalarKind: QueryScalarKind.FloatingPoint }))
        {
            return AggregateFloatingValues(aggregate, numericValues, cancellationToken);
        }
        decimal[] numbers = QueryCancellation.Observe(numericValues, cancellationToken).Select(value =>
            value is LiteralValue { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal } literal &&
            Decimal.TryParse(literal.LexicalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number)
                ? number : throw new ArgumentException("Numeric aggregates require finite numeric inputs or null.")).ToArray();
        if (aggregate.Function is GraphPatternAggregateFunction.PercentileContinuous or GraphPatternAggregateFunction.PercentileDiscrete)
        {
            if (numbers.Length == 0)
            {
                return UnboundValue.Instance;
            }
            QueryComparisonSort.Sort(numbers, (left, right) => left.CompareTo(right), cancellationToken);
            decimal percentile = aggregate.Percentile!.Value;
            decimal quantile;
            if (aggregate.Function == GraphPatternAggregateFunction.PercentileDiscrete)
            {
                quantile = numbers[Math.Max(0, (int)Decimal.Ceiling(percentile * numbers.Length) - 1)];
            }
            else
            {
                decimal position = percentile * (numbers.Length - 1);
                int lower = (int)Decimal.Floor(position), upper = (int)Decimal.Ceiling(position);
                decimal fraction = position - lower;
                quantile = numbers[lower] * (1 - fraction) + numbers[upper] * fraction;
            }
            return new LiteralValue(quantile.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Decimal);
        }
        if (aggregate.Function is GraphPatternAggregateFunction.StandardDeviationSample or GraphPatternAggregateFunction.StandardDeviationPopulation)
        {
            if (numbers.Length == 0)
            {
                return UnboundValue.Instance;
            }
            if (numbers.Length == 1)
            {
                return aggregate.Function == GraphPatternAggregateFunction.StandardDeviationSample
                    ? UnboundValue.Instance : GraphScalarEvaluator.FloatingValue(0);
            }
            double mean = 0, squared = 0;
            int count = 0;
            foreach (decimal number in QueryCancellation.Observe(numbers, cancellationToken))
            {
                double delta = (double)number - mean;
                mean += delta / ++count;
                squared += delta * ((double)number - mean);
            }
            double deviation = Math.Sqrt(Math.Max(0, squared) /
                (count - (aggregate.Function == GraphPatternAggregateFunction.StandardDeviationSample ? 1 : 0)));
            return GraphScalarEvaluator.FloatingValue(deviation);
        }
        throw new ArgumentException("Unsupported numeric aggregate.");
    }

    internal static int CompareAggregateValues(QueryValue left, QueryValue right) =>
        left is ListValue first && right is ListValue second
            ? GraphScalarEvaluator.CompareOrderedLists(first, second, CompareAggregateScalarValues)
            : CompareAggregateScalarValues(left, right);

    private static int CompareAggregateScalarValues(QueryValue left, QueryValue right)
    {
        if (left is NodeValue a && right is NodeValue b)
        {
            return StringComparer.Ordinal.Compare(a.Identity.ToString(), b.Identity.ToString());
        }
        int Rank(QueryValue value) => value switch
        {
            NodeValue => 0,
            ListValue => 2,
            LiteralValue { ScalarKind: QueryScalarKind.Point } => 4,
            LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime } => 5,
            LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } => 6,
            LiteralValue { ScalarKind: QueryScalarKind.Date } => 8,
            LiteralValue { ScalarKind: QueryScalarKind.ZonedTime } => 9,
            LiteralValue { ScalarKind: QueryScalarKind.LocalTime } => 10,
            LiteralValue { ScalarKind: QueryScalarKind.Duration } => 12,
            LiteralValue { ScalarKind: QueryScalarKind.String } => 14,
            LiteralValue { ScalarKind: QueryScalarKind.Boolean } => 16,
            LiteralValue { ScalarKind: QueryScalarKind.Guid } => 17,
            LiteralValue { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint } => 18,
            NullValue or UnboundValue => 20,
            _ => throw new ArgumentException("Unsupported grouped result ordering type.")
        };
        int rank = Rank(left).CompareTo(Rank(right));
        return rank != 0 ? rank : GraphScalarEvaluator.CompareForOrder(left, right);
    }

    private static QueryValue AggregateFloatingValues(GraphPatternAggregate aggregate, QueryValue[] values,
        CancellationToken cancellationToken)
    {
        double[] numbers = QueryCancellation.Observe(values, cancellationToken).Select(value => GraphScalarEvaluator.FloatingNumber(value, out double number)
            ? number : throw new ArgumentException("Numeric aggregates require numeric inputs or null.")).ToArray();
        if (numbers.Length == 0)
        {
            return UnboundValue.Instance;
        }
        if (aggregate.Function is GraphPatternAggregateFunction.PercentileContinuous or GraphPatternAggregateFunction.PercentileDiscrete)
        {
            QueryComparisonSort.Sort(values, GraphScalarEvaluator.CompareForOrder, cancellationToken);
            if (aggregate.Function == GraphPatternAggregateFunction.PercentileDiscrete)
            {
                return values[Math.Max(0, (int)Decimal.Ceiling(aggregate.Percentile!.Value * values.Length) - 1)];
            }
            double position = (double)aggregate.Percentile!.Value * (values.Length - 1);
            int lower = (int)Math.Floor(position), upper = (int)Math.Ceiling(position);
            GraphScalarEvaluator.FloatingNumber(values[lower], out double a);
            GraphScalarEvaluator.FloatingNumber(values[upper], out double b);
            return GraphScalarEvaluator.FloatingValue(lower == upper ? a : a * (upper - position) + b * (position - lower));
        }
        if (aggregate.Function is GraphPatternAggregateFunction.StandardDeviationSample or GraphPatternAggregateFunction.StandardDeviationPopulation)
        {
            if (numbers.Length == 1)
            {
                return aggregate.Function == GraphPatternAggregateFunction.StandardDeviationSample
                    ? UnboundValue.Instance : GraphScalarEvaluator.FloatingValue(0);
            }
            double mean = 0, squared = 0;
            int count = 0;
            foreach (double number in QueryCancellation.Observe(numbers, cancellationToken))
            {
                double delta = number - mean;
                mean += delta / ++count;
                squared += delta * (number - mean);
            }
            return GraphScalarEvaluator.FloatingValue(Math.Sqrt(Math.Max(0, squared) /
                (count - (aggregate.Function == GraphPatternAggregateFunction.StandardDeviationSample ? 1 : 0))));
        }
        throw new ArgumentException("Unsupported floating-point aggregate.");
    }
}
