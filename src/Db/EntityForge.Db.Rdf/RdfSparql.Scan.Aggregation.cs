using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EntityForge.Db.Query.Planning;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static IExternalSort<OrderedSolution> CreateScanResultSort(ParsedSelectQuery plan, ExternalSortDiskBudget budget) =>
        plan.Distinct ? new ExternalDistinctSort<OrderedSolution>(
            row => JsonSerializer.Serialize(ProjectRow(row.Row, plan.Projection).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()),
            (row, stream) => JsonSerializer.Serialize(stream, row),
            bytes => JsonSerializer.Deserialize<OrderedSolution>(bytes) ?? throw new InvalidDataException("Missing distinct SPARQL row."),
            plan.Modifiers.Order.Count != 0 ? Comparer<OrderedSolution>.Create((left, right) => CompareOrdered(left, right, plan.Modifiers)) : null,
            budget) : new ExternalMergeSort<OrderedSolution>(
                Comparer<OrderedSolution>.Create((left, right) => CompareOrdered(left, right, plan.Modifiers)),
                (row, stream) => JsonSerializer.Serialize(stream, row),
                bytes => JsonSerializer.Deserialize<OrderedSolution>(bytes) ?? throw new InvalidDataException("Missing sorted SPARQL row."),
                maximumDiskBytes: Int64.MaxValue, sharedDiskBudget: budget);

    private sealed record ScanAggregateContribution(Dictionary<string, RdfTerm> Group, int Index, RdfTerm? Term, string OrderKey, long Sequence);

    private sealed class CountGroupIdentity : IEqualityComparer<Dictionary<string, RdfTerm>>
    {
        internal static CountGroupIdentity Instance { get; } = new();

        public bool Equals(Dictionary<string, RdfTerm>? left, Dictionary<string, RdfTerm>? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }
            if (left is null || right is null || left.Count != right.Count)
            {
                return false;
            }
            foreach (var pair in left)
            {
                if (!right.TryGetValue(pair.Key, out var term) || !Object.Equals(pair.Value, term))
                {
                    return false;
                }
            }
            return true;
        }

        public int GetHashCode(Dictionary<string, RdfTerm> value)
        {
            int hash = 0;
            foreach (var pair in value)
            {
                hash ^= HashCode.Combine(StringComparer.Ordinal.GetHashCode(pair.Key), pair.Value);
            }
            return hash;
        }
    }

    private sealed class ScanAggregateGroup(Dictionary<string, RdfTerm> group, ParsedSelectQuery plan, RdfSparqlReadView view)
    {
        private readonly AggregateAccumulator[] _aggregates = plan.Projection.Aggregates
            .Select(item => new AggregateAccumulator(item with { Distinct = false }, view)).ToArray();

        internal void Accumulate(ScanAggregateContribution row)
        {
            if (row.Index < 0)
            {
                return;
            }
            _aggregates[row.Index].AccumulateCaptured(row.Term);
        }

        internal void AccumulateCount(long count) => _aggregates[0].AccumulateCount(count);

        internal Dictionary<string, RdfTerm> FinalizeRow()
        {
            var row = new Dictionary<string, RdfTerm>(group, StringComparer.Ordinal);
            foreach (var aggregate in _aggregates)
            {
                if (aggregate.FinalizeValue() is { } value)
                {
                    row[aggregate.Projection.Alias] = value;
                }
            }
            return row;
        }
    }

    private sealed class ScanAggregateSort : IExternalSort<OrderedSolution>
    {
        private readonly ParsedSelectQuery _plan;
        private readonly RdfSparqlReadView _view;
        private readonly ExternalGroupReduce<ScanAggregateContribution, ScanAggregateGroup> _groups;
        private readonly IExternalSort<OrderedSolution> _results;
        private readonly ExternalDistinctSort<ScanAggregateContribution>? _distinct;
        private readonly ExternalCountGroups<Dictionary<string, RdfTerm>>? _counts;
        private readonly string[] _groupVariables;
        private long _sequence;

        internal ScanAggregateSort(ParsedSelectQuery plan, RdfSparqlReadView view, ExternalSortDiskBudget budget)
        {
            _plan = plan;
            _view = view;
            _groupVariables = plan.Modifiers.GroupVariables.Distinct(StringComparer.Ordinal).ToArray();
            if (plan.Projection.Aggregates is [{ Function: "COUNT", Value: null, Distinct: false }])
            {
                _counts = new(group => JsonSerializer.Serialize(group.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()),
                    (group, stream) => JsonSerializer.Serialize(stream, group),
                    bytes => JsonSerializer.Deserialize<Dictionary<string, RdfTerm>>(bytes) ?? throw new InvalidDataException("Missing count group."), budget,
                    identityComparer: CountGroupIdentity.Instance);
            }
            _groups = new(row => JsonSerializer.Serialize(row.Group.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()),
                (row, stream) => JsonSerializer.Serialize(stream, row),
                bytes => JsonSerializer.Deserialize<ScanAggregateContribution>(bytes) ?? throw new InvalidDataException("Missing aggregate input."),
                row => new ScanAggregateGroup(row.Group, plan, view), (state, row) => state.Accumulate(row), budget);
            if (plan.Projection.Aggregates.Any(item => item.Distinct))
            {
                string Key(ScanAggregateContribution row)
                {
                    if (!plan.Projection.Aggregates[row.Index].Distinct)
                    {
                        return "all:" + row.Sequence.ToString(CultureInfo.InvariantCulture);
                    }
                    string group = JsonSerializer.Serialize(row.Group.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
                    return "distinct:" + group.Length.ToString(CultureInfo.InvariantCulture) + ":" + group + row.OrderKey;
                }
                _distinct = new(Key, (row, stream) => JsonSerializer.Serialize(stream, row),
                    bytes => JsonSerializer.Deserialize<ScanAggregateContribution>(bytes) ?? throw new InvalidDataException("Missing distinct aggregate input."),
                    Comparer<ScanAggregateContribution>.Create((left, right) => left.Sequence.CompareTo(right.Sequence)), budget);
            }
            _results = CreateScanResultSort(plan, budget);
        }

        public async ValueTask AddAsync(OrderedSolution row, CancellationToken cancellation)
        {
            var grouped = new Dictionary<string, RdfTerm>(_groupVariables.Length, StringComparer.Ordinal);
            foreach (string name in _groupVariables)
            {
                if (row.Row.TryGetValue(name, out var term))
                {
                    grouped.Add(name, term);
                }
            }
            if (_counts is not null)
            {
                await _counts.AddAsync(grouped, cancellation).ConfigureAwait(false);
                return;
            }
            if (_plan.Projection.Aggregates.Count == 0)
            {
                await _groups.AddAsync(new(grouped, -1, null, String.Empty, checked(_sequence++)), cancellation).ConfigureAwait(false);
            }
            for (int index = 0; index < _plan.Projection.Aggregates.Count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                var aggregate = _plan.Projection.Aggregates[index];
                var term = aggregate.Value is null ? null : EvaluateValue(aggregate.Value, row.Row, _view);
                string identity = !aggregate.Distinct ? String.Empty : aggregate.Value is null
                    ? JsonSerializer.Serialize(row.Row.Where(pair => !pair.Key.StartsWith('$')).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray())
                    : JsonSerializer.Serialize(term);
                var contribution = new ScanAggregateContribution(grouped, index, term,
                    index.ToString("D8", CultureInfo.InvariantCulture) + ":" + identity, checked(_sequence++));
                if (_distinct is null)
                {
                    await _groups.AddAsync(contribution, cancellation).ConfigureAwait(false);
                }
                else
                {
                    await _distinct.AddAsync(contribution, cancellation).ConfigureAwait(false);
                }
            }
        }

        private async ValueTask FinishAsync(ScanAggregateGroup group, CancellationToken cancellation)
        {
            var row = group.FinalizeRow();
            if (_plan.Modifiers.Having is { } having && Evaluate(_view, having, HavingRow(row, _plan.Projection)) != FilterResult.True)
            {
                return;
            }
            row = ApplyComputedProjection([row], _plan.Projection, _view)[0];
            await _results.AddAsync(OrderSolution(row, _plan.Modifiers, _view), cancellation).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<OrderedSolution> ReadAsync([EnumeratorCancellation] CancellationToken cancellation = default)
        {
            if (_distinct is not null)
            {
                await foreach (var row in _distinct.ReadAsync(cancellation).ConfigureAwait(false))
                {
                    await _groups.AddAsync(row, cancellation).ConfigureAwait(false);
                }
                await _distinct.DisposeAsync().ConfigureAwait(false);
            }
            bool populated = false;
            if (_counts is not null)
            {
                await foreach (var count in _counts.ReadAsync(cancellation).ConfigureAwait(false))
                {
                    populated = true;
                    var group = new ScanAggregateGroup(count.Value, _plan, _view);
                    group.AccumulateCount(count.Count);
                    await FinishAsync(group, cancellation).ConfigureAwait(false);
                }
                await _counts.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await foreach (var group in _groups.ReadAsync(cancellation).ConfigureAwait(false))
                {
                    populated = true;
                    await FinishAsync(group, cancellation).ConfigureAwait(false);
                }
            }
            if (!populated && _plan.Modifiers.GroupVariables.Count == 0)
            {
                await FinishAsync(new(new Dictionary<string, RdfTerm>(), _plan, _view), cancellation).ConfigureAwait(false);
            }
            await _groups.DisposeAsync().ConfigureAwait(false);
            await foreach (var row in _results.ReadAsync(cancellation).ConfigureAwait(false))
            {
                yield return row;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _results.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await _groups.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (_distinct is not null)
                    {
                        await _distinct.DisposeAsync().ConfigureAwait(false);
                    }
                    if (_counts is not null)
                    {
                        await _counts.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
    }
}
