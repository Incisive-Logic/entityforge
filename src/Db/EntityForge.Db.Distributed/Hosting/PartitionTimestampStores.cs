using System.Text.Json;
using System.Security.Cryptography;
using EntityForge.Db.Kernel;
using dotnet_etcd;
using Etcdserverpb;
using Google.Protobuf;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed class EtcdTimestampStore(EtcdClient client, string key, string domain) : IDatabaseTimestampStore
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private const int MaximumObservations = 8;
    private const int DeletionBatch = 64;
    private readonly object _sync = new();

    private sealed record StoredTicket(DatabaseTimestampTicket Value, int Bytes);

    private readonly SortedDictionary<long, Dictionary<Guid, StoredTicket>> _observations = [];
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly Dictionary<Guid, StoredTicket> _tickets = [];
    private long _observedRevision, _rootCreation;
    private ulong? _cluster;

    private sealed record Envelope(string Domain, DatabaseTimestampState State);

    public string Identity => "timestamp/2/" + domain + "/" + key;

    private string TicketKey(Guid id) => key + "/ticket/" + id.ToString("N");

    internal static ByteString PrefixEnd(string prefix)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(prefix);
        for (int i = bytes.Length - 1; i >= 0; i--)
        {
            if (bytes[i] != byte.MaxValue)
            {
                bytes[i]++;
                return ByteString.CopyFrom(bytes, 0, i + 1);
            }
        }

        throw new ArgumentException("Unbounded timestamp namespace.");
    }

    private void RequireCluster(ulong value)
    {
        lock (_sync)
        {
            if (_cluster is { } cluster && cluster != value)
            {
                throw new InvalidDataException("Timestamp cluster changed.");
            }

            _cluster = value;
        }
    }

    public async ValueTask<DatabaseTimestampObservation> ReadAsync(CancellationToken token)
    {
        await _readGate.WaitAsync(token);
        try
        {
            // The head is always read freshly. The second range shares its linearizable
            // transaction snapshot and returns only ticket revisions not already observed.
            // Cached ticket values are deeply immutable; this is not cached authority.
            string prefix = key + "/ticket/";
            var read = new TxnRequest();
            read.Success.Add(new RequestOp { RequestRange = new RangeRequest { Key = ByteString.CopyFromUtf8(key) } });
            read.Success.Add(new RequestOp
            {
                RequestRange = new RangeRequest
                {
                    Key = ByteString.CopyFromUtf8(prefix),
                    RangeEnd = PrefixEnd(prefix),
                    MinModRevision = _observedRevision == 0 ? 0 : checked(_observedRevision + 1)
                }
            });
            var result = await client.TransactionAsync(read, cancellationToken: token);
            RequireCluster(result.Header.ClusterId);
            var head = result.Responses[0].ResponseRange.Kvs.SingleOrDefault();
            var changed = result.Responses[1].ResponseRange.Kvs;
            if (head is null)
            {
                if (_rootCreation != 0 || changed.Count != 0)
                {
                    throw new InvalidDataException("Timestamp head disappeared.");
                }

                lock (_sync)
                {
                    _observations[0] = [];
                }

                return new(0, DatabaseTimestampState.Empty);
            }
            if (_rootCreation != 0 && _rootCreation != head.CreateRevision || head.Lease != 0 || head.Value.Length > MaximumBytes)
            {
                throw new InvalidDataException("Timestamp root association changed.");
            }

            var envelope = JsonSerializer.Deserialize<Envelope>(head.Value.Span) ?? throw new InvalidDataException("Missing timestamp state.");
            if (envelope.Domain != domain || envelope.State.Tickets.Length != 0)
            {
                throw new InvalidDataException("Timestamp domain or format changed.");
            }

            foreach (var item in changed)
            {
                if (item.Lease != 0 || item.Value.Length > MaximumBytes)
                {
                    throw new InvalidDataException("Invalid timestamp ticket.");
                }

                var ticket = JsonSerializer.Deserialize<DatabaseTimestampTicket>(item.Value.Span) ?? throw new InvalidDataException("Missing timestamp ticket.");
                if (item.Key.ToStringUtf8() != TicketKey(ticket.Operation))
                {
                    throw new InvalidDataException("Timestamp ticket identity differs.");
                }

                _tickets[ticket.Operation] = new(ticket, item.Value.Length);
            }
            var active = _tickets.Values.Where(t => !EntityOperationIds.IsBefore(t.Value.Operation, envelope.State.Retired)).ToArray();
            if (active.Sum(t => (long)t.Bytes) + head.Value.Length > MaximumBytes)
            {
                throw new InvalidDataException("Timestamp state capacity exceeded.");
            }

            _rootCreation = head.CreateRevision;
            _observedRevision = result.Header.Revision;
            lock (_sync)
            {
                _observations[head.ModRevision] = new(_tickets);
                while (_observations.Count > MaximumObservations)
                {
                    _observations.Remove(_observations.Keys.First());
                }
            }
            return new(head.ModRevision, envelope.State with
            {
                Tickets = active.Select(t => t.Value).OrderBy(t => t.Timestamp).ToArray()
            });
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async ValueTask<bool> ReplaceAsync(long revision, DatabaseTimestampState state, CancellationToken token)
    {
        Dictionary<Guid, StoredTicket>? before;
        lock (_sync)
        {
            _observations.TryGetValue(revision, out before);
        }

        if (before is null)
        {
            if ((await ReadAsync(token)).Revision != revision)
            {
                return false;
            }

            lock (_sync)
            {
                before = _observations.GetValueOrDefault(revision);
            }

            if (before is null)
            {
                return false;
            }
        }
        var request = new TxnRequest();
        var encoded = ByteString.CopyFromUtf8(key);
        request.Compare.Add(new Compare
        {
            Key = encoded,
            Target = Compare.Types.CompareTarget.Mod,
            Result = Compare.Types.CompareResult.Equal,
            ModRevision = revision
        });
        byte[] header = JsonSerializer.SerializeToUtf8Bytes(new Envelope(domain, state with
        {
            Tickets = []
        }));
        long total = header.Length;
        request.Success.Add(new RequestOp { RequestPut = new PutRequest { Key = encoded, Value = ByteString.CopyFrom(header) } });
        foreach (var ticket in state.Tickets)
        {
            if (before.TryGetValue(ticket.Operation, out var old) && Same(old.Value, ticket))
            {
                total += old.Bytes;
                continue;
            }
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(ticket);
            total += bytes.Length;
            request.Success.Add(new RequestOp { RequestPut = new PutRequest { Key = ByteString.CopyFromUtf8(TicketKey(ticket.Operation)), Value = ByteString.CopyFrom(bytes) } });
        }
        if (total > MaximumBytes || request.Success.Count > DeletionBatch)
        {
            throw new InvalidOperationException("Timestamp mutation capacity exceeded.");
        }

        var result = await client.TransactionAsync(request, cancellationToken: token);
        RequireCluster(result.Header.ClusterId);
        if (!result.Succeeded)
        {
            return false;
        }
        // The durable floor makes old identities inadmissible before physical key deletion.
        // Every deletion batch compares that exact head revision; a concurrent writer stops cleanup.
        var retained = state.Tickets.Select(t => t.Operation).ToHashSet();
        foreach (var batch in before.Keys.Where(id => !retained.Contains(id)).Chunk(DeletionBatch))
        {
            var cleanup = new TxnRequest();
            cleanup.Compare.Add(new Compare
            {
                Key = encoded,
                Target = Compare.Types.CompareTarget.Mod,
                Result = Compare.Types.CompareResult.Equal,
                ModRevision = result.Header.Revision
            });
            foreach (Guid id in batch)
            {
                cleanup.Success.Add(new RequestOp { RequestDeleteRange = new DeleteRangeRequest { Key = ByteString.CopyFromUtf8(TicketKey(id)) } });
            }

            var deleted = await client.TransactionAsync(cleanup, cancellationToken: token);
            RequireCluster(deleted.Header.ClusterId);
            if (!deleted.Succeeded)
            {
                break;
            }

            await _readGate.WaitAsync(token);
            try
            {
                foreach (Guid id in batch)
                {
                    _tickets.Remove(id);
                }
            }
            finally
            {
                _readGate.Release();
            }
        }
        return true;
    }

    private static bool Same(DatabaseTimestampTicket left, DatabaseTimestampTicket right) =>
        ReferenceEquals(left, right) || left.Timestamp == right.Timestamp && left.Operation == right.Operation &&
        left.Descriptor == right.Descriptor && left.Rejected == right.Rejected && left.Participants.SequenceEqual(right.Participants) &&
        left.Applied.Count == right.Applied.Count && left.Applied.All(p => right.Applied.TryGetValue(p.Key, out var v) && v == p.Value);

}

internal sealed class FileTimestampStore : IDatabaseTimestampStore, IDisposable
{
    private readonly string _path;
    private readonly FileStream _ownership;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _faulted;

    public string Identity
    {
        get;
    }

    internal FileTimestampStore(string directory, string domain)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(Path.GetFullPath(directory), "timestamp.json");
        _ownership = new(Path.Combine(directory, "timestamp.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Identity = "timestamp/1/" + domain;
    }

    private sealed record Envelope(string Identity, long Revision, DatabaseTimestampState State);

    public async ValueTask<DatabaseTimestampObservation> ReadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            return await ReadCore(token);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<DatabaseTimestampObservation> ReadCore(CancellationToken token)
    {
        if (_faulted)
        {
            throw new IOException("Timestamp publication requires reopen.");
        }

        if (!File.Exists(_path))
        {
            return new(0, DatabaseTimestampState.Empty);
        }

        var value = await PartitionMetadataFile.ReadAsync<Envelope>(_path, token, 4 * 1024 * 1024);
        if (value.Identity != Identity)
        {
            throw new InvalidDataException("Timestamp file domain differs.");
        }

        return new(value.Revision, value.State);
    }

    public async ValueTask<bool> ReplaceAsync(long revision, DatabaseTimestampState state, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var old = await ReadCore(token);
            if (old.Revision != revision)
            {
                return false;
            }

            try
            {
                string replacement = _path + ".replacement";
                if (File.Exists(replacement))
                {
                    File.Delete(replacement);
                }

                await PartitionMetadataFile.WriteNewAsync(replacement, new Envelope(Identity, checked(revision + 1), state), token, 4 * 1024 * 1024);
                File.Move(replacement, _path, true);
                EntityForge.Db.Storage.DurableDirectory.Sync(Path.GetDirectoryName(_path)!);
            }
            catch
            {
                _faulted = true;
                throw;
            }
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _ownership.Dispose();
        _gate.Dispose();
    }
}
