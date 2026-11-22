using EntityForge.Db.Query.Adapters;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace EntityForge.Db.Distributed.Hosting;

// Pages retain an immutable completed projection, not unowned native pins. Tokens bind
// the exact request and offset. Restart/expiry rejects a token rather than refreshing its snapshot.
internal sealed class PartitionQueryPages(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private const int MaximumEntries = 32;
    private const int MaximumRetainedBytes = 16 * 1024 * 1024;
    private const int MaximumPageSize = 10_000;
    private const int IdentityBytes = 16;
    private const int OffsetBytes = sizeof(int);
    private const int DigestBytes = 32;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(DigestBytes);
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly object _gate = new();
    private int _bytes;

    private sealed record Entry(PartitionQueryResult Result, byte[] Request, DateTimeOffset Expires, int Bytes);

    private static byte[] Fingerprint(PartitionQueryRequest request) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, PartitionParticipantWire.Json));

    internal PartitionQueryResult Start(PartitionQueryRequest request, PartitionQueryResult result, int size)
    {
        ValidateSize(size);
        int count = result.NativeResult is PortableEntityQueryResponse typed ? typed.Entities.Count : result.Value.GetProperty("entities").GetArrayLength();
        if (count <= size)
        {
            return result;
        }

        int bytes = checked((int)PartitionProjectionValue.Measure(result, MaximumRetainedBytes));
        lock (_gate)
        {
            Expire();
            if (_entries.Count >= MaximumEntries || bytes > MaximumRetainedBytes - _bytes)
            {
                throw new InvalidOperationException("Distributed query cursor capacity is exhausted.");
            }

            var id = Guid.NewGuid();
            var entry = new Entry(result, Fingerprint(request), _time.GetUtcNow() + Lifetime, bytes);
            _entries.Add(id, entry);
            _bytes += bytes;
            return Page(id, entry, 0, size);
        }
    }

    internal PartitionQueryResult Resume(PartitionQueryRequest request, string token, int size)
    {
        ValidateSize(size);
        if (token.Length > 128)
        {
            throw new InvalidDataException("Invalid query continuation.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(token);
        }
        catch (FormatException e)
        {
            throw new InvalidDataException("Invalid query continuation.", e);
        }
        const int bodyLength = IdentityBytes + OffsetBytes;
        if (bytes.Length != bodyLength + DigestBytes || !CryptographicOperations.FixedTimeEquals(
            HMACSHA256.HashData(_key, bytes.AsSpan(0, bodyLength)), bytes.AsSpan(bodyLength)))
        {
            throw new InvalidDataException("Invalid query continuation.");
        }

        Guid id = new(bytes.AsSpan(0, IdentityBytes));
        int offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(IdentityBytes));
        lock (_gate)
        {
            Expire();
            if (!_entries.TryGetValue(id, out var entry))
            {
                throw new PartitionQueryCursorExpiredException();
            }

            if (!CryptographicOperations.FixedTimeEquals(entry.Request, Fingerprint(request)))
            {
                throw new InvalidDataException("Continuation belongs to a different query.");
            }

            return Page(id, entry, offset, size);
        }
    }

    private PartitionQueryResult Page(Guid id, Entry entry, int offset, int size)
    {
        var typed = entry.Result.NativeResult as PortableEntityQueryResponse;
        var rows = typed is null ? entry.Result.Value.GetProperty("entities") : default;
        int count = typed?.Entities.Count ?? rows.GetArrayLength();
        if (offset < 0 || offset >= count)
        {
            throw new InvalidDataException("Invalid query continuation offset.");
        }

        int end = Math.Min(checked(offset + size), count);
        string? next = null;
        if (end < count)
        {
            byte[] bytes = new byte[IdentityBytes + OffsetBytes + DigestBytes];
            id.TryWriteBytes(bytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(IdentityBytes), end);
            HMACSHA256.HashData(_key, bytes.AsSpan(0, IdentityBytes + OffsetBytes)).CopyTo(bytes, IdentityBytes + OffsetBytes);
            next = Convert.ToBase64String(bytes);
        }
        if (typed is not null)
        {
            var page = typed with
            {
                Entities = typed.Entities.Skip(offset).Take(end - offset).ToArray(),
                NextContinuation = null
            };
            return entry.Result.WithOutput(PartitionPublicQueryProjection.EntityPage(page)) with
            {
                NextContinuation = next
            };
        }
        var properties = entry.Result.Value.EnumerateObject().Where(p => p.Name is not ("entities" or "nextContinuation"))
            .ToDictionary(p => p.Name, p => p.Value);
        properties["entities"] = JsonSerializer.SerializeToElement(rows.EnumerateArray().Skip(offset).Take(end - offset));
        return entry.Result with
        {
            Value = JsonSerializer.SerializeToElement(properties),
            NextContinuation = next
        };
    }

    internal static void ValidateSize(int size)
    {
        if (size is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }
    }

    private void Expire()
    {
        var now = _time.GetUtcNow();
        foreach (var pair in _entries.Where(p => p.Value.Expires <= now).ToArray())
        {
            _entries.Remove(pair.Key);
            _bytes -= pair.Value.Bytes;
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _bytes = 0;
        }
    }
}

/// <summary>The retained query projection expired; starting again selects a new snapshot explicitly.</summary>
public sealed class PartitionQueryCursorExpiredException : Exception
{
    /// <summary>Creates an exception indicating that the query continuation's retained snapshot is no longer available.</summary>
    public PartitionQueryCursorExpiredException() : base("The distributed query continuation has expired.")
    {
    }
}
