using System.Runtime.CompilerServices;
using System.Text;
using System.Globalization;
using EntityForge.Db.Graph;
using EntityForge.Db.Query.Planning;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlScan
{
    private sealed record AggregateScanValues(QueryValue[] Values, QueryValue[] Order, int AggregateIndex = -1, long Sequence = 0);

    private sealed class CountGroupIdentity(int groupColumns) : IEqualityComparer<AggregateScanValues>
    {
        public bool Equals(AggregateScanValues? left, AggregateScanValues? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }
            if (left is null || right is null)
            {
                return false;
            }
            for (int index = 0; index < groupColumns; index++)
            {
                if (!SameValue(left.Values[index], right.Values[index]))
                {
                    return false;
                }
            }
            return true;
        }

        public int GetHashCode(AggregateScanValues value)
        {
            var hash = new HashCode();
            for (int index = 0; index < groupColumns; index++)
            {
                QueryValue item = value.Values[index];
                hash.Add(item is LiteralValue or NullValue or UnboundValue
                    ? item.GetHashCode() : StringComparer.Ordinal.GetHashCode(GraphScalarEvaluator.DistinctKey(item)));
            }
            return hash.ToHashCode();
        }

        private static bool SameValue(QueryValue left, QueryValue right)
        {
            if (left is LiteralValue first && right is LiteralValue second)
            {
                // Uri.Equals can equate different spellings. Stay finer than the
                // canonical key, including duration datatype text, on this fast path.
                return first == second && StringComparer.Ordinal.Equals(
                    first.DatatypeIdentifier?.OriginalString, second.DatatypeIdentifier?.OriginalString);
            }
            if (left is LiteralValue or NullValue or UnboundValue || right is LiteralValue or NullValue or UnboundValue)
            {
                return Object.Equals(left, right);
            }
            return StringComparer.Ordinal.Equals(GraphScalarEvaluator.DistinctKey(left), GraphScalarEvaluator.DistinctKey(right));
        }
    }

    private sealed class AggregateScanState(QueryValue[] keys, GraphPatternAggregation plan)
    {
        internal QueryValue[] Keys { get; } = keys;

        internal BoundedGraphPatternExecutor.StreamingAggregate[] Accumulators
        {
            get;
        }
        =
            plan.Aggregates.Select(item => new BoundedGraphPatternExecutor.StreamingAggregate(item.Function)).ToArray();
    }

    private static void EncodeAggregateScanValues(AggregateScanValues row, Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(row.AggregateIndex);
        writer.Write(row.Sequence);
        foreach (var values in new[] { row.Values, row.Order })
        {
            writer.Write(values.Length);
            foreach (var value in values)
            {
                QueryScalarBinaryCodec.Write(writer, value);
            }
        }
    }

    private static AggregateScanValues DecodeAggregateScanValues(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        int aggregateIndex = reader.ReadInt32();
        long sequence = reader.ReadInt64();
        QueryValue[] Read()
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > stream.Length - stream.Position)
            {
                throw new InvalidDataException("Invalid aggregate scan value count.");
            }
            var values = new QueryValue[count];
            for (int index = 0; index < count; index++)
            {
                values[index] = QueryScalarBinaryCodec.Read(reader);
            }
            return values;
        }
        var row = new AggregateScanValues(Read(), Read(), aggregateIndex, sequence);
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Unexpected aggregate scan payload.");
        }
        return row;
    }

    private static async IAsyncEnumerable<GqlScanPage> AggregateScanAsync(CompiledGraphGqlQuery compiled,
        GraphGqlQueryRequest request, Func<CancellationToken, IAsyncEnumerable<GqlScanInputPage>> source,
        GqlScanBudget budget, long offset, long limit, [EnumeratorCancellation] CancellationToken cancellation,
        Func<CancellationToken, IAsyncEnumerable<GqlEdgeScanInputPage>>? edgeSource = null,
        Func<GraphPatternAggregation, bool, CancellationToken, IAsyncEnumerable<AggregateInputPage>>? preparedSource = null)
    {
        var matchedBindings = new HashSet<string>([compiled.Query.Start.Binding], StringComparer.Ordinal);
        foreach (var relationship in compiled.Query.Relationships)
        {
            matchedBindings.Add(relationship.Binding);
            matchedBindings.Add(relationship.Target.Binding);
        }
        var stages = compiled.Pipeline?.Stages;
        if (stages?.FirstOrDefault() is { Operation: "PROJECT", Aggregation: null, Distinct: false } identity &&
            identity.Terms is { } identityTerms && identityTerms.Count == matchedBindings.Count && identityTerms.All(term => matchedBindings.Contains(term.Alias) &&
                term.Expression is { Binding: "__row", Function: null } && term.Expression.Property == term.Alias))
        {
            // The compiler's initial identity projection retains the matched elements.
            stages = stages!.Skip(1).ToArray();
        }
        var project = stages?.FirstOrDefault();
        var plan = compiled.Query.Aggregation ?? project?.Aggregation;
        if (plan is null || compiled.OmitsResult || compiled.RequiresCompleteGraph || compiled.Union is not null ||
            compiled.ReturnsEntityGraph || (preparedSource is not null ? compiled.Query.Relationships.Count is < 2 or > 64 ||
                compiled.Query.Relationships.Any(item => !item.Hops.IsFixed || item.MatchKind != GraphPatternMatchKind.Required) :
                edgeSource is null ? compiled.Query.Relationships.Count != 0 :
                compiled.Query.Relationships.Count != 1 || !compiled.Query.Relationships[0].Hops.IsFixed ||
                compiled.Query.Relationships[0].MatchKind != GraphPatternMatchKind.Required) ||
            stages is not null && (project?.Operation != "PROJECT" || stages.Count(stage => stage.Operation == "ORDER BY") > 1) ||
            plan.GroupBy.Any(key => key.Binding is not null) ||
            plan.Aggregates.Any(item => !BoundedGraphPatternExecutor.StreamingAggregate.Supports(item.Function) ||
                item.ValueBinding is not null && item.ValueProperty is null && item.Function != GraphPatternAggregateFunction.Count))
        {
            throw new NotSupportedException("GQL aggregate scans require a node or supported fixed required relationships, scalar grouping and COUNT/SUM/AVG/MIN/MAX, with optional ordering. Other aggregate plans require a separate execution strategy.");
        }
        bool Local(GraphScalarExpression expression, bool allowNode = false) =>
            (expression.Binding != "__row" || allowNode && expression.Property is { } binding && matchedBindings.Contains(binding)) &&
            (expression.Arguments?.All(argument => Local(argument)) ?? true);
        if (plan.GroupBy.Any(key => !Local(key.Expression!)) ||
            plan.Aggregates.Any(item => item.Expression is { } value && !Local(value, item.Function == GraphPatternAggregateFunction.Count)))
        {
            throw new NotSupportedException("GQL aggregate scans require local property/scalar aggregate arguments.");
        }
        if (budget.MaximumSortTemporaryBytes == 0)
        {
            throw new NotSupportedException("GQL aggregate scans require an enabled temporary-sort budget.");
        }
        var order = stages?.LastOrDefault(stage => stage.Operation == "ORDER BY")?.Order ??
            plan.OrderBy.Select(term => (new GraphScalarExpression("__row", term.Alias, null),
                term.Direction == GraphPatternSortDirection.Descending, (bool?)null)).ToArray();
        string[] aliases = plan.GroupBy.Select(key => key.Alias).Concat(plan.Aggregates.Select(item => item.Alias)).ToArray();
        var projection = PlanAggregateProjection(stages, aliases);
        string[] outputAliases = projection.Aliases.Select(alias => compiled.OutputNames?.GetValueOrDefault(alias, alias) ?? alias).ToArray();
        var disk = new ExternalSortDiskBudget(budget.MaximumSortTemporaryBytes);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(budget.Timeout ?? TimeSpan.FromMinutes(5));
        bool countOnly = plan.Aggregates is [
        {
            Function: GraphPatternAggregateFunction.Count, Distinct: false,
            ValueBinding: null, ValueProperty: null, Expression: null
        }];
        await using var counts = countOnly ? new ExternalCountGroups<AggregateScanValues>(
            row => GqlRowPipeline.RowKey(row.Values.Take(plan.GroupBy.Count)), EncodeAggregateScanValues, DecodeAggregateScanValues, disk,
            identityComparer: new CountGroupIdentity(plan.GroupBy.Count)) : null;
        await using var groups = new ExternalGroupReduce<AggregateScanValues, AggregateScanState>(
            row => GqlRowPipeline.RowKey(row.Values.Take(plan.GroupBy.Count)), EncodeAggregateScanValues, DecodeAggregateScanValues,
            row => new(row.Values.Take(plan.GroupBy.Count).ToArray(), plan),
            (state, row) =>
            {
                if (row.AggregateIndex >= 0)
                {
                    state.Accumulators[row.AggregateIndex].Add(row.Values[plan.GroupBy.Count + row.AggregateIndex]);
                    return;
                }
                for (int index = 0; index < state.Accumulators.Length; index++)
                {
                    state.Accumulators[index].Add(row.Values[plan.GroupBy.Count + index]);
                }
            }, disk);
        string ContributionKey(AggregateScanValues row)
        {
            if (!plan.Aggregates[row.AggregateIndex].Distinct)
            {
                return "all:" + row.Sequence.ToString(CultureInfo.InvariantCulture);
            }
            string group = GqlRowPipeline.RowKey(row.Values.Take(plan.GroupBy.Count));
            string value = GqlRowPipeline.RowKey([row.Values[plan.GroupBy.Count + row.AggregateIndex]]);
            return "distinct:" + group.Length.ToString(CultureInfo.InvariantCulture) + ":" + group +
                row.AggregateIndex.ToString(CultureInfo.InvariantCulture) + ":" + value;
        }
        await using var distinct = plan.Aggregates.Any(item => item.Distinct)
            ? new ExternalDistinctSort<AggregateScanValues>(ContributionKey, EncodeAggregateScanValues, DecodeAggregateScanValues,
                Comparer<AggregateScanValues>.Create((left, right) => left.Sequence.CompareTo(right.Sequence)), disk) : null;
        long sequence = 0;
        int Compare(AggregateScanValues left, AggregateScanValues right)
        {
            for (int index = 0; index < order.Count; index++)
            {
                var first = left.Order[index];
                var second = right.Order[index];
                var term = order[index];
                bool firstNull = first is NullValue or UnboundValue, secondNull = second is NullValue or UnboundValue;
                if (firstNull != secondNull && term.Item3 is { } nullsFirst)
                {
                    return firstNull == nullsFirst ? -1 : 1;
                }
                int comparison = compiled.Query.Aggregation is not null ? BoundedGraphPatternExecutor.CompareAggregateValues(first, second) :
                    GraphScalarEvaluator.CompareForOrder(first, second);
                if (comparison != 0)
                {
                    return term.Item2 ? -comparison : comparison;
                }
            }
            return StringComparer.Ordinal.Compare(GqlRowPipeline.RowKey(left.Values), GqlRowPipeline.RowKey(right.Values));
        }
        await using IExternalSort<AggregateScanValues> results = projection.Distinct
            ? new ExternalDistinctSort<AggregateScanValues>(row => GqlRowPipeline.RowKey(row.Values), EncodeAggregateScanValues,
                DecodeAggregateScanValues, order.Count == 0 ? null : Comparer<AggregateScanValues>.Create(Compare), disk)
            : new ExternalMergeSort<AggregateScanValues>(Comparer<AggregateScanValues>.Create(Compare), EncodeAggregateScanValues,
                DecodeAggregateScanValues, maximumDiskBytes: Int64.MaxValue, sharedDiskBudget: disk);
        DatabaseVersion? version = null;
        long examined = 0;
        bool complete = false;
        var inputs = preparedSource is null
            ? ReadAggregateInputs(compiled.Query, plan, source, edgeSource, budget, limit != 0, deadline.Token)
            : preparedSource(plan, limit != 0, deadline.Token);
        await foreach (var page in inputs.ConfigureAwait(false))
        {
            version ??= page.Version;
            if (version != page.Version || request.SnapshotVersion is { } requested && requested != page.Version.Value)
            {
                throw new InvalidDataException("GQL aggregate source violated its snapshot or bounded-page contract.");
            }
            if (limit == 0)
            {
                yield return new(page.Version, [], true);
                yield break;
            }
            examined += page.ExaminedElements;
            if (examined > budget.MaximumExaminedElements)
            {
                throw new QueryScanWorkLimitException("GQL aggregate scan exceeded its examined-element budget.");
            }
            foreach (var captured in page.Rows)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var values = aliases.Select(alias => captured.Bindings[alias]).ToArray();
                if (distinct is null)
                {
                    if (counts is not null)
                    {
                        await counts.AddAsync(new(values, []), deadline.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await groups.AddAsync(new(values, []), deadline.Token).ConfigureAwait(false);
                    }
                }
                else
                {
                    for (int index = 0; index < plan.Aggregates.Count; index++)
                    {
                        await distinct.AddAsync(new(values, [], index, checked(sequence++)), deadline.Token).ConfigureAwait(false);
                    }
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
            throw new InvalidDataException("GQL aggregate source ended without completion.");
        }
        if (distinct is not null)
        {
            await foreach (var contribution in distinct.ReadAsync(deadline.Token).ConfigureAwait(false))
            {
                await groups.AddAsync(contribution, deadline.Token).ConfigureAwait(false);
            }
            await distinct.DisposeAsync().ConfigureAwait(false);
        }
        bool populated = false, nullEliminated = false;
        async ValueTask Finish(AggregateScanState state)
        {
            QueryValue[] values = [.. state.Keys, .. state.Accumulators.Select(item => item.Finish())];
            nullEliminated |= state.Accumulators.Any(item => item.NullEliminated);
            var projected = projection.Apply(new(aliases.Zip(values)
                .Select(pair => new KeyValuePair<string, QueryValue>(pair.First, pair.Second)), []), deadline.Token);
            if (projected is null)
            {
                return;
            }
            var bindings = projected.Bindings;
            var keys = order.Select(term => GraphScalarEvaluator.Evaluate(term.Item1,
                (binding, name) => binding == "__row" ? bindings.GetValueOrDefault(name, UnboundValue.Instance) : UnboundValue.Instance)).ToArray();
            await results.AddAsync(new(projection.Aliases.Select(alias => bindings[alias]).ToArray(), keys), deadline.Token).ConfigureAwait(false);
        }
        if (counts is not null)
        {
            await foreach (var group in counts.ReadAsync(deadline.Token).ConfigureAwait(false))
            {
                populated = true;
                var state = new AggregateScanState(group.Value.Values.Take(plan.GroupBy.Count).ToArray(), plan);
                state.Accumulators[0].AddCount(group.Count);
                await Finish(state).ConfigureAwait(false);
            }
            await counts.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            await foreach (var state in groups.ReadAsync(deadline.Token).ConfigureAwait(false))
            {
                populated = true;
                await Finish(state).ConfigureAwait(false);
            }
        }
        if (!populated && plan.GroupBy.Count == 0)
        {
            await Finish(new([], plan)).ConfigureAwait(false);
        }
        await groups.DisposeAsync().ConfigureAwait(false);
        long skipped = 0, emitted = 0;
        var rows = new List<GraphPatternMatch>();
        IReadOnlyList<QueryResultDiagnostic> diagnostics = nullEliminated ? [BoundedGraphPatternExecutor.NullEliminatedDiagnostic] : [];
        await foreach (var row in results.ReadAsync(deadline.Token).ConfigureAwait(false))
        {
            if (emitted == limit)
            {
                break;
            }
            if (skipped++ < offset)
            {
                continue;
            }
            if (++emitted > budget.MaximumRows)
            {
                throw new QueryScanWorkLimitException("GQL aggregate scan exceeded its result-row budget.");
            }
            rows.Add(new(outputAliases.Zip(row.Values).Select(pair => new KeyValuePair<string, QueryValue>(pair.First, pair.Second)), []));
            if (rows.Count == budget.PageSize)
            {
                yield return new(version!.Value, rows, false, diagnostics);
                diagnostics = [];
                rows = [];
            }
        }
        yield return new(version!.Value, rows, true, diagnostics);
    }
}
