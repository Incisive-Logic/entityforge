using System.Runtime.CompilerServices;
using System.Text;

namespace EntityForge.Db.Query.Planning;

// The language supplies an exact equivalence key, separately from value ordering.
// Choosing the least ordered representative preserves ORDER -> project -> DISTINCT.
internal sealed class ExternalDistinctSort<T> : IExternalSort<T>
{
    private sealed record Keyed(string Key, T Value);

    private readonly Func<T, string> _key;
    private readonly Action<T, Stream> _encode;
    private readonly Func<byte[], T> _decode;
    private readonly IComparer<T>? _order;
    private readonly ExternalSortDiskBudget _budget;
    private readonly int _bufferedRows;
    private readonly ExternalMergeSort<Keyed> _byKey;

    internal ExternalDistinctSort(Func<T, string> key, Action<T, Stream> encode, Func<byte[], T> decode,
        IComparer<T>? order, ExternalSortDiskBudget budget, int maximumBufferedRows = 4096)
    {
        _key = key;
        _encode = encode;
        _decode = decode;
        _order = order;
        _budget = budget;
        _bufferedRows = maximumBufferedRows;
        _byKey = new(Comparer<Keyed>.Create((left, right) =>
        {
            int comparison = StringComparer.Ordinal.Compare(left.Key, right.Key);
            return comparison != 0 ? comparison : _order?.Compare(left.Value, right.Value) ?? 0;
        }), Encode, Decode, maximumBufferedRows: maximumBufferedRows, maximumDiskBytes: Int64.MaxValue,
            sharedDiskBudget: budget);
    }

    private void Encode(Keyed row, Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(row.Key);
        _encode(row.Value, stream);
    }

    private Keyed Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        string key = reader.ReadString();
        return new(key, _decode(reader.ReadBytes(checked((int)(stream.Length - stream.Position)))));
    }

    public ValueTask AddAsync(T value, CancellationToken cancellation) => _byKey.AddAsync(new(_key(value), value), cancellation);

    public async IAsyncEnumerable<T> ReadAsync([EnumeratorCancellation] CancellationToken cancellation = default)
    {
        await using var ordered = _order is null ? null : new ExternalMergeSort<T>(_order, _encode, _decode,
            maximumBufferedRows: _bufferedRows, maximumDiskBytes: Int64.MaxValue, sharedDiskBudget: _budget);
        string? previous = null;
        await foreach (var row in _byKey.ReadAsync(cancellation).ConfigureAwait(false))
        {
            if (StringComparer.Ordinal.Equals(previous, row.Key))
            {
                continue;
            }
            previous = row.Key;
            if (ordered is null)
            {
                yield return row.Value;
            }
            else
            {
                await ordered.AddAsync(row.Value, cancellation).ConfigureAwait(false);
            }
        }
        await _byKey.DisposeAsync().ConfigureAwait(false);
        if (ordered is not null)
        {
            await foreach (var row in ordered.ReadAsync(cancellation).ConfigureAwait(false))
            {
                yield return row;
            }
        }
    }

    public ValueTask DisposeAsync() => _byKey.DisposeAsync();
}
