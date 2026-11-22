using System.Runtime.CompilerServices;
using System.Text;

namespace EntityForge.Db.Query.Planning;

// COUNT(*) is associative over bag contributions. Keep a bounded table of group
// identities, spill partial counts, then merge them with the existing reducer.
// Language-specific equality and group serialization remain caller supplied.
internal sealed class ExternalCountGroups<T> : IAsyncDisposable where T : notnull
{
    internal sealed class Group(T value)
    {
        internal T Value { get; } = value;

        internal long Count
        {
            get; set;
        }
    }

    private sealed record Partial(string Key, T Value, long Count);

    private readonly Func<T, string> _key;
    private readonly Action<T, Stream> _encode;
    private readonly Func<byte[], T> _decode;
    private readonly ExternalGroupReduce<Partial, Group> _spill;
    private readonly Dictionary<string, Group> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<T, Group>? _identities;
    private readonly int _maximumGroups;
    private readonly int _maximumBytes;
    private int _bytes;
    private bool _reading, _disposed, _faulted;

    internal ExternalCountGroups(Func<T, string> key, Action<T, Stream> encode, Func<byte[], T> decode,
        ExternalSortDiskBudget budget, int maximumGroups = 4096, int maximumBytes = 8 * 1024 * 1024,
        IEqualityComparer<T>? identityComparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumGroups);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        _key = key;
        _encode = encode;
        _decode = decode;
        _maximumGroups = maximumGroups;
        _maximumBytes = maximumBytes;
        // Equality may be finer than the canonical key, but must never equate
        // different canonical keys. Only owned decoded group values are retained.
        _identities = identityComparer is null ? null : new(identityComparer);
        _spill = new(row => row.Key, (row, stream) =>
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(row.Key);
            writer.Write(row.Count);
            encode(row.Value, stream);
        }, bytes =>
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            string identity = reader.ReadString();
            long count = reader.ReadInt64();
            if (count <= 0)
            {
                throw new InvalidDataException("Invalid partial group count.");
            }
            return new(identity, decode(reader.ReadBytes(checked((int)(stream.Length - stream.Position)))), count);
        }, row => new(row.Value), (state, row) => state.Count = checked(state.Count + row.Count), budget);
    }

    internal async ValueTask AddAsync(T value, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reading || _faulted)
        {
            throw new InvalidOperationException("Cannot append after reading or a failed append.");
        }
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (_identities is not null && _identities.TryGetValue(value, out var existing))
            {
                existing.Count = checked(existing.Count + 1);
                return;
            }
            string key = _key(value);
            if (_pending.TryGetValue(key, out var group))
            {
                group.Count = checked(group.Count + 1);
                return;
            }
            using var encoded = new BoundedSortRecordStream(Math.Min(1024 * 1024, _maximumBytes));
            _encode(value, encoded);
            byte[] bytes = encoded.ToArray();
            long cost = bytes.Length + (long)key.Length * sizeof(char) + (_identities is null ? 64 : 128);
            if (cost > _maximumBytes)
            {
                throw new QuerySortBudgetException("Count group identity exceeds its buffered byte budget.");
            }
            if (_pending.Count == _maximumGroups || cost > _maximumBytes - _bytes)
            {
                await FlushAsync(cancellation).ConfigureAwait(false);
            }
            var captured = new Group(_decode(bytes)) { Count = 1 };
            _pending.Add(key, captured);
            _identities?.Add(captured.Value, captured);
            _bytes += (int)cost;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private async ValueTask FlushAsync(CancellationToken cancellation)
    {
        foreach (var pair in _pending)
        {
            await _spill.AddAsync(new(pair.Key, pair.Value.Value, pair.Value.Count), cancellation).ConfigureAwait(false);
        }
        _pending.Clear();
        _identities?.Clear();
        _bytes = 0;
    }

    internal async IAsyncEnumerable<Group> ReadAsync([EnumeratorCancellation] CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reading || _faulted)
        {
            throw new InvalidOperationException("Count groups can be read only once after successful appends.");
        }
        _reading = true;
        await FlushAsync(cancellation).ConfigureAwait(false);
        await foreach (var group in _spill.ReadAsync(cancellation).ConfigureAwait(false))
        {
            yield return group;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _pending.Clear();
        _identities?.Clear();
        _bytes = 0;
        await _spill.DisposeAsync().ConfigureAwait(false);
    }
}
