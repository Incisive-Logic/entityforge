using System.Buffers.Binary;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;

namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionSemanticRequests
{
    private static readonly AsyncLocal<Scope?> Current = new();

    // Private to one invocation. Byte equality, rather than a digest alone,
    // protects reuse even if a caller recovers and mutates a backing array.
    internal sealed class Scope : IDisposable
    {
        private readonly Scope? before = Current.Value;
        private readonly object gate = new();
        private readonly Dictionary<string, (byte[] Bytes, SemanticEntityRequest Request)> entries = new();
        private int retainedBytes;
        private bool closed;

        internal Scope() => Current.Value = this;

        internal IDisposable Activate()
        {
            var previous = Current.Value;
            Current.Value = this;
            return new RestoreScope(() => Current.Value = previous);
        }

        internal SemanticEntityRequest Read(ReadOnlySpan<byte> bytes)
        {
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            lock (gate)
            {
                if (!closed && entries.TryGetValue(key, out var existing) && bytes.SequenceEqual(existing.Bytes))
                {
                    return existing.Request;
                }

                var owned = bytes.ToArray();
                var request = RestoreCore(owned);
                if (!closed && owned.Length <= PartitionApplicationBinding.MaximumRequestBytes - retainedBytes && !entries.ContainsKey(key))
                {
                    entries.Add(key, (owned, request));
                    retainedBytes += owned.Length;
                }
                return request;
            }
        }

        public void Dispose()
        {
            Current.Value = before;
            lock (gate)
            {
                closed = true;
                entries.Clear();
                retainedBytes = 0;
            }
        }
    }

    internal sealed class RestoreScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    internal static SemanticEntityRequest Restore(ReadOnlySpan<byte> bytes) => Current.Value is { } scope
        ? scope.Read(bytes) : RestoreCore(bytes);

    private static SemanticEntityRequest RestoreCore(ReadOnlySpan<byte> bytes)
    {
        using var measured = DistributedReadTelemetry.Measure("partition.semantic-request.restore");
        return SemanticEntityRequest.Restore(bytes);
    }

    private static void ValidateBounds(IReadOnlyList<ReadOnlyMemory<byte>> requests)
    {
        if (requests.Count is < 1 or > DurableDatabaseCommit.MaximumTransactionContributions ||
            requests.Any(r => r.IsEmpty) || requests.Sum(r => (long)r.Length) > PartitionApplicationBinding.MaximumRequestBytes)
        {
            throw new InvalidDataException("Invalid Semantic transaction requests.");
        }
    }

    internal static byte[] Encode(IReadOnlyList<ReadOnlyMemory<byte>> requests)
    {
        ValidateBounds(requests);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(requests.Count);
        foreach (var request in requests)
        {
            writer.Write(request.Length);
            writer.Write(request.Span);
        }
        return stream.ToArray();
    }

    internal static ReadOnlyMemory<byte>[] Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < sizeof(int))
        {
            throw new InvalidDataException("Missing Semantic transaction requests.");
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (count is < 1 or > DurableDatabaseCommit.MaximumTransactionContributions ||
            bytes.Length > PartitionApplicationBinding.MaximumRequestBytes + (count + 1) * sizeof(int))
        {
            throw new InvalidDataException("Invalid Semantic transaction request bound.");
        }

        int offset = sizeof(int);
        var result = new ReadOnlyMemory<byte>[count];
        for (int index = 0; index < count; index++)
        {
            if (bytes.Length - offset < sizeof(int))
            {
                throw new InvalidDataException("Truncated Semantic transaction request.");
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
            offset += sizeof(int);
            if (length <= 0 || length > bytes.Length - offset)
            {
                throw new InvalidDataException("Invalid Semantic request length.");
            }

            result[index] = bytes.Slice(offset, length).ToArray();
            offset += length;
        }
        if (offset != bytes.Length || result.Sum(r => (long)r.Length) > PartitionApplicationBinding.MaximumRequestBytes)
        {
            throw new InvalidDataException("Semantic transaction request framing differs.");
        }

        return result;
    }

    internal static void Validate(Guid generation, Guid operation, IReadOnlyList<ReadOnlyMemory<byte>> requests)
    {
        ValidateBounds(requests);
        foreach (var bytes in requests)
        {
            var request = Restore(bytes.Span);
            if (request.OperationId != operation || request._metadata.DatabaseIncarnation != generation)
            {
                throw new InvalidDataException("Semantic transaction request association differs.");
            }
        }
    }
}
