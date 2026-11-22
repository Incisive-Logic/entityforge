using System.Runtime.CompilerServices;
using System.Text;

namespace EntityForge.Db.Query.Planning;

// Language callbacks own grouping equivalence and accumulator semantics. Only one
// group's state is retained; callers must bound that state's own memory use.
internal sealed class ExternalGroupReduce<T, TState> : IAsyncDisposable
{
    private sealed record Keyed(string Key, long Sequence, T Value);

    private readonly Func<T, string> _key;
    private readonly Func<T, TState> _create;
    private readonly Action<TState, T> _accumulate;
    private readonly ExternalMergeSort<Keyed> _rows;
    private long _sequence;

    internal ExternalGroupReduce(Func<T, string> key, Action<T, Stream> encode, Func<byte[], T> decode,
        Func<T, TState> create, Action<TState, T> accumulate, ExternalSortDiskBudget budget)
    {
        _key = key;
        _create = create;
        _accumulate = accumulate;
        _rows = new(Comparer<Keyed>.Create((left, right) =>
        {
            int comparison = StringComparer.Ordinal.Compare(left.Key, right.Key);
            return comparison == 0 ? left.Sequence.CompareTo(right.Sequence) : comparison;
        }), (row, stream) =>
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(row.Key);
            writer.Write(row.Sequence);
            encode(row.Value, stream);
        }, bytes =>
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            string groupKey = reader.ReadString();
            long sequence = reader.ReadInt64();
            return new(groupKey, sequence, decode(reader.ReadBytes(checked((int)(stream.Length - stream.Position)))));
        }, maximumDiskBytes: Int64.MaxValue, sharedDiskBudget: budget);
    }

    internal ValueTask AddAsync(T row, CancellationToken cancellation) =>
        _rows.AddAsync(new(_key(row), checked(_sequence++), row), cancellation);

    internal async IAsyncEnumerable<TState> ReadAsync([EnumeratorCancellation] CancellationToken cancellation)
    {
        string? previous = null;
        TState state = default!;
        bool populated = false;
        await foreach (var row in _rows.ReadAsync(cancellation).ConfigureAwait(false))
        {
            if (!populated || !StringComparer.Ordinal.Equals(previous, row.Key))
            {
                if (populated)
                {
                    yield return state;
                }
                state = _create(row.Value);
                previous = row.Key;
                populated = true;
            }
            _accumulate(state, row.Value);
        }
        if (populated)
        {
            yield return state;
        }
    }

    public ValueTask DisposeAsync() => _rows.DisposeAsync();
}
