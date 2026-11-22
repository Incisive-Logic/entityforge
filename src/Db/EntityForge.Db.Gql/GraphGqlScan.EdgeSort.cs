using System.Runtime.CompilerServices;
using EntityForge.Db.Graph;
using EntityForge.Db.Query.Planning;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlScan
{
    private sealed class EdgeScanSort : IAsyncDisposable
    {
        private readonly CompiledGraphGqlQuery _compiled;
        private readonly IReadOnlyList<GraphPatternOrderTerm> _order;
        private readonly string[] _aliases;
        private readonly string[] _keys;
        private readonly IExternalSort<AggregateScanValues> _sort;
        private long _sequence;

        internal EdgeScanSort(CompiledGraphGqlQuery compiled, IReadOnlyList<GraphPatternOrderTerm> order, int maximumBytes)
        {
            _compiled = compiled;
            _order = order;
            _aliases = compiled.Projection!.Terms.Select(term => compiled.OutputNames?.GetValueOrDefault(term.Alias, term.Alias) ?? term.Alias).ToArray();
            string prefix = "__scan_order_" + Guid.NewGuid().ToString("N") + "_";
            _keys = Enumerable.Range(0, order.Count).Select(index => prefix + index).ToArray();
            var comparer = Comparer<AggregateScanValues>.Create((left, right) =>
            {
                for (int index = 0; index < order.Count; index++)
                {
                    int comparison = GraphScalarEvaluator.CompareForOrder(left.Order[index], right.Order[index]);
                    if (comparison != 0)
                    {
                        return order[index].Direction == GraphPatternSortDirection.Descending ? -comparison : comparison;
                    }
                }
                return left.Sequence.CompareTo(right.Sequence);
            });
            var disk = new ExternalSortDiskBudget(maximumBytes);
            _sort = compiled.Query.Distinct
                ? new ExternalDistinctSort<AggregateScanValues>(row => GqlRowPipeline.RowKey(row.Values),
                    EncodeAggregateScanValues, DecodeAggregateScanValues, order.Count == 0 ? null : comparer, disk)
                : new ExternalMergeSort<AggregateScanValues>(comparer, EncodeAggregateScanValues,
                    DecodeAggregateScanValues, maximumDiskBytes: Int64.MaxValue, sharedDiskBudget: disk);
        }

        internal GraphPatternMatch Project(Func<string, string, QueryValue> property)
        {
            var projected = GraphGqlPropertyProjectionShaper.Project(_compiled.Projection!, property);
            var keys = _order.Select((term, index) =>
            {
                // An ORDER BY projected alias must reuse its already evaluated value,
                // rather than evaluating the projected expression again for sorting.
                var alias = term.Expression is null ? null : _compiled.Projection!.Terms
                    .FirstOrDefault(item => ReferenceEquals(item.Expression, term.Expression));
                QueryValue value = alias is not null ? projected.Bindings[alias.Alias] : term.Expression is { } expression
                    ? GraphScalarEvaluator.Evaluate(expression, property) : property(term.Binding, term.Property);
                return new KeyValuePair<string, QueryValue>(_keys[index], value);
            });
            return new(projected.Bindings.Concat(keys), []);
        }

        internal ValueTask AddAsync(GraphPatternMatch row, CancellationToken cancellation) =>
            _sort.AddAsync(new(_aliases.Select(alias => row.Bindings[alias]).ToArray(),
                _keys.Select(key => row.Bindings[key]).ToArray(), Sequence: checked(_sequence++)), cancellation);

        internal async IAsyncEnumerable<GraphPatternMatch> ReadAsync([EnumeratorCancellation] CancellationToken cancellation)
        {
            await foreach (var row in _sort.ReadAsync(cancellation).ConfigureAwait(false))
            {
                yield return new(_aliases.Zip(row.Values).Select(pair =>
                    new KeyValuePair<string, QueryValue>(pair.First, pair.Second)), []);
            }
        }

        public ValueTask DisposeAsync() => _sort.DisposeAsync();
    }
}
