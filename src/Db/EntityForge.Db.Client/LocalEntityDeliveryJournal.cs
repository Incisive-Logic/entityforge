using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EntityForge.Db.Client;

/// <summary>The durable stages of a client delivery.</summary>
public enum EntityDeliveryState
{
    /// <summary>The envelope is retained but no delivery receipt has been recorded.</summary>
    Pending,

    /// <summary>The receipt is retained; source acknowledgement has not been recorded as complete.</summary>
    Delivered,

    /// <summary>Source acknowledgement completed and its completion was recorded locally.</summary>
    Acknowledged
}

/// <summary>Identifies a retained delivery and its durable stage.</summary>
/// <param name="DeliveryId">Identity of the retained delivery envelope.</param>
/// <param name="State">Last stage recorded durably in the local journal.</param>
public sealed record EntityDeliveryStatus(Guid DeliveryId, EntityDeliveryState State);

/// <summary>
/// A bounded, exclusive local journal of opaque prepared envelopes and receipts. Callbacks must not
/// reenter this journal. Acknowledgement callbacks must be idempotent because external acknowledgement
/// and recording its completion cannot be atomic.
/// </summary>
public sealed class LocalEntityDeliveryJournal : IAsyncDisposable
{
    /// <summary>Maximum retained prepared-envelope size in bytes.</summary>
    public const int MaximumEnvelopeBytes = 66 * 1024 * 1024;

    /// <summary>Maximum serialized delivery-receipt size in bytes.</summary>
    public const int MaximumReceiptBytes = 1024 * 1024;

    /// <summary>Maximum retained source-acknowledgement context size in bytes.</summary>
    public const int MaximumSourceContextBytes = 64 * 1024;

    /// <summary>Maximum number of deliveries retained by one journal.</summary>
    public const int MaximumDeliveries = 128;

    /// <summary>Maximum journal-file size in bytes before another append is refused.</summary>
    public const long MaximumFileBytes = 256L * 1024 * 1024;
    private const int FramingBytes = 8;
    private const int HeaderBytes = FramingBytes + ChecksumBytes;
    private const int IdentityBytes = 16;
    private const int ChecksumBytes = 32;
    private const int RecordOverhead = HeaderBytes + 1 + IdentityBytes + ChecksumBytes;
    private const int LengthBytes = sizeof(int);
    private const uint Magic = 0x314A4645; // EFJ1
    private FileStream _file;
    private readonly FileStream? _ownership;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _enqueueAdmission = new();
    private long _enqueueBytes;
    private int _enqueueCount;
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly List<Guid> _order = [];
    private bool _unusable;

    internal Action<string>? Boundary
    {
        get; set;
    }

    private LocalEntityDeliveryJournal(FileStream file, FileStream? ownership = null)
    {
        _file = file;
        _path = file.Name;
        _ownership = ownership;
    }

    private static FileStream OpenData(string path, FileMode mode) => new(path, mode, FileAccess.ReadWrite,
        FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);

    /// <summary>Opens an exclusively owned journal, recovering complete records and discarding only an incomplete trailing record.</summary>
    public static async ValueTask<LocalEntityDeliveryJournal> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        var ownership = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        LocalEntityDeliveryJournal? journal = null;
        try
        {
            if (!File.Exists(path) && File.Exists(path + ".compact"))
            {
                throw new InvalidDataException("The authoritative journal is missing; an orphan replacement cannot establish ownership of its history.");
            }

            journal = new(OpenData(path, FileMode.OpenOrCreate), ownership);
            await journal.RecoverAsync(cancellationToken).ConfigureAwait(false);
            // The authoritative file always wins; an unpublished replacement is never promoted on recovery.
            if (File.Exists(path + ".compact"))
            {
                File.Delete(path + ".compact");
            }

            return journal;
        }
        catch
        {
            try
            {
                if (journal is not null)
                {
                    await journal._file.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await ownership.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>Durably retains exact envelope bytes and opaque source acknowledgement context before delivery.</summary>
    /// <remarks>Inputs are copied before the first asynchronous wait. Concurrent enqueues are rejected
    /// when their combined input exceeds the file byte limit or delivery count limit; callers may retry
    /// after outstanding enqueues complete.</remarks>
    public async ValueTask EnqueueAsync(Guid deliveryId, ReadOnlyMemory<byte> envelope,
        ReadOnlyMemory<byte> sourceContext = default, CancellationToken cancellationToken = default)
    {
        if (deliveryId == Guid.Empty)
        {
            throw new ArgumentException("A delivery identity is required.", nameof(deliveryId));
        }

        if (envelope.Length is < 1 or > MaximumEnvelopeBytes || sourceContext.Length > MaximumSourceContextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope));
        }

        cancellationToken.ThrowIfCancellationRequested();
        long inputBytes = (long)envelope.Length + sourceContext.Length;
        lock (_enqueueAdmission)
        {
            if (_enqueueCount >= MaximumDeliveries || inputBytes > MaximumFileBytes - _enqueueBytes)
            {
                throw new InvalidOperationException("The journal concurrent enqueue limit is reached.");
            }

            _enqueueCount++;
            _enqueueBytes += inputBytes;
        }
        try
        {
            byte[] frozen = envelope.ToArray(), context = sourceContext.ToArray();
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CheckUsable();
                if (_entries.TryGetValue(deliveryId, out Entry? existing))
                {
                    if (!existing.Envelope.AsSpan().SequenceEqual(frozen) || !existing.Context.AsSpan().SequenceEqual(context))
                    {
                        throw new InvalidOperationException("A delivery identity cannot be reused with different content.");
                    }

                    return;
                }
                if (_entries.Count >= MaximumDeliveries)
                {
                    throw new InvalidOperationException("The journal delivery limit is reached.");
                }

                byte[] payload = new byte[LengthBytes + frozen.Length + context.Length];
                BinaryPrimitives.WriteInt32LittleEndian(payload, frozen.Length);
                frozen.CopyTo(payload, LengthBytes);
                context.CopyTo(payload, LengthBytes + frozen.Length);
                await AppendAsync(EntityDeliveryState.Pending, deliveryId, payload,
                    MaximumReceiptBytes + 2 * RecordOverhead, cancellationToken).ConfigureAwait(false);
                _entries.Add(deliveryId, new(frozen, context));
                _order.Add(deliveryId);
                Observe("Enqueued");
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            lock (_enqueueAdmission)
            {
                _enqueueCount--;
                _enqueueBytes -= inputBytes;
            }
        }
    }

    /// <summary>
    /// Sends retained bytes and flushes the returned receipt before reporting success. The callback must
    /// return only a validated durable receipt. Exceptions propagate and leave the delivery pending.
    /// A retained receipt suppresses further sends, including after reopen.
    /// </summary>
    public async ValueTask<byte[]> DeliverAsync(Guid deliveryId,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> send, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(send);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckUsable();
            Entry entry = _entries[deliveryId];
            if (entry.Receipt is not null)
            {
                return entry.Receipt.ToArray();
            }

            byte[] receipt = (await send(entry.Envelope.ToArray(), cancellationToken).ConfigureAwait(false))?.ToArray()
                ?? throw new InvalidOperationException("Delivery returned no durable receipt.");
            if (receipt.Length is < 1 or > MaximumReceiptBytes)
            {
                throw new InvalidOperationException("The receipt exceeds the journal bounds.");
            }

            await AppendAsync(EntityDeliveryState.Delivered, deliveryId, receipt,
                -(MaximumReceiptBytes + RecordOverhead), cancellationToken).ConfigureAwait(false);
            entry.Receipt = receipt;
            Observe("ReceiptRecorded");
            return receipt.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Calls source acknowledgement only after a durable receipt exists, then durably records completion.</summary>
    public async ValueTask AcknowledgeAsync(Guid deliveryId,
        Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, CancellationToken, ValueTask> acknowledge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledge);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckUsable();
            Entry entry = _entries[deliveryId];
            if (entry.Acknowledged)
            {
                return;
            }

            if (entry.Receipt is null)
            {
                throw new InvalidOperationException("Delivery must have a durable receipt before acknowledgement.");
            }

            await acknowledge(entry.Context.ToArray(), entry.Receipt.ToArray(), cancellationToken).ConfigureAwait(false);
            Observe("SourceAcknowledged");
            await AppendAsync(EntityDeliveryState.Acknowledged, deliveryId, [], -RecordOverhead, cancellationToken).ConfigureAwait(false);
            entry.Acknowledged = true;
            Observe("AcknowledgementRecorded");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lists retained deliveries in enqueue order, including completed identities retained for deduplication.</summary>
    public async ValueTask<IReadOnlyList<EntityDeliveryStatus>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckUsable();
            return Array.AsReadOnly(_order.Select(id => new EntityDeliveryStatus(id,
                _entries[id].Acknowledged ? EntityDeliveryState.Acknowledged : _entries[id].Receipt is null ? EntityDeliveryState.Pending : EntityDeliveryState.Delivered)).ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    private long ReservedBytes => _entries.Values.Sum(entry => entry.Acknowledged ? 0L :
        entry.Receipt is null ? MaximumReceiptBytes + 2L * RecordOverhead : RecordOverhead);

    /// <summary>
    /// Atomically rewrites the journal, forgetting only the selected acknowledged deliveries. Forgotten IDs
    /// lose local deduplication protection and must never be reused by the source. Other entries retain their
    /// exact envelopes, contexts, receipts and order. Cancellation before publication keeps the old journal.
    /// </summary>
    public async ValueTask CompactAsync(IReadOnlyCollection<Guid> acknowledgedToForget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgedToForget);
        if (acknowledgedToForget.Count > MaximumDeliveries)
        {
            throw new ArgumentOutOfRangeException(nameof(acknowledgedToForget));
        }

        var forget = acknowledgedToForget.ToHashSet();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckUsable();
            if (forget.Any(id => !_entries.TryGetValue(id, out Entry? entry) || !entry.Acknowledged))
            {
                throw new InvalidOperationException("Compaction can forget only acknowledged deliveries.");
            }

            string temporary = _path + ".compact";
            try
            {
                await using (var replacement = new LocalEntityDeliveryJournal(OpenData(temporary, FileMode.CreateNew)))
                {
                    replacement.Boundary = Boundary is null ? null : stage =>
                    {
                        if (stage == "AppendHeaderWritten")
                        {
                            Observe("CompactionAppendHeaderWritten");
                        }
                    };
                    foreach (Guid id in _order.Where(id => !forget.Contains(id)))
                    {
                        Entry entry = _entries[id];
                        await replacement.EnqueueAsync(id, entry.Envelope, entry.Context, cancellationToken).ConfigureAwait(false);
                        if (entry.Receipt is not null)
                        {
                            await replacement.DeliverAsync(id, (_, _) => ValueTask.FromResult(entry.Receipt), cancellationToken).ConfigureAwait(false);
                        }

                        if (entry.Acknowledged)
                        {
                            await replacement.AcknowledgeAsync(id, (_, _, _) => ValueTask.CompletedTask, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    replacement._file.Flush(flushToDisk: true);
                }
                Observe("CompactionPrepared");
                cancellationToken.ThrowIfCancellationRequested();
                // Publish by same-directory rename, as the database's other retained files do.
                // File.Replace also merges destination metadata and can fail on Windows even
                // when both data handles are closed. The stable ownership handle remains locked
                // across close/rename/reopen; no other journal owner can enter this window.
                await _file.DisposeAsync().ConfigureAwait(false);
                File.Move(temporary, _path, overwrite: true);
                Observe("CompactionPublished");
                _file = OpenData(_path, FileMode.Open);
                _file.Position = _file.Length;
                foreach (Guid id in forget)
                {
                    _entries.Remove(id);
                }

                _order.RemoveAll(forget.Contains);
            }
            catch
            {
                _unusable = true;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask AppendAsync(EntityDeliveryState state, Guid id, byte[] payload, long reservationChange, CancellationToken token)
    {
        int length = RecordOverhead + payload.Length;
        if (_file.Length + length + ReservedBytes + reservationChange > MaximumFileBytes)
        {
            throw new InvalidOperationException("The journal is full; receipt and acknowledgement capacity is reserved for accepted deliveries.");
        }

        byte[] record = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(sizeof(uint)), length);
        SHA256.HashData(record.AsSpan(0, FramingBytes), record.AsSpan(FramingBytes, ChecksumBytes));
        record[HeaderBytes] = (byte)state;
        id.TryWriteBytes(record.AsSpan(HeaderBytes + 1, IdentityBytes));
        payload.CopyTo(record, HeaderBytes + 1 + IdentityBytes);
        SHA256.HashData(record.AsSpan(0, length - ChecksumBytes), record.AsSpan(length - ChecksumBytes));
        try
        {
            await _file.WriteAsync(record.AsMemory(0, HeaderBytes), token).ConfigureAwait(false);
            Observe("AppendHeaderWritten");
            await _file.WriteAsync(record.AsMemory(HeaderBytes), token).ConfigureAwait(false);
            _file.Flush(flushToDisk: true);
        }
        catch
        {
            _unusable = true;
            throw;
        } // An uncertain append must be recovered before another operation.
    }

    private async ValueTask RecoverAsync(CancellationToken token)
    {
        if (_file.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("Journal exceeds its file limit.");
        }

        byte[] header = new byte[HeaderBytes];
        while (_file.Position < _file.Length)
        {
            long start = _file.Position;
            if (_file.Length - start < HeaderBytes)
            {
                TruncateTail(start);
                break;
            }
            await _file.ReadExactlyAsync(header, token).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(sizeof(uint)));
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(header.AsSpan(0, FramingBytes)), header.AsSpan(FramingBytes)) ||
                BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic || length < RecordOverhead ||
                length > RecordOverhead + LengthBytes + MaximumEnvelopeBytes + MaximumSourceContextBytes)
            {
                throw new InvalidDataException("Invalid journal framing.");
            }

            if (_file.Length - start < length)
            {
                TruncateTail(start);
                break;
            }
            byte[] record = new byte[length];
            header.CopyTo(record, 0);
            await _file.ReadExactlyAsync(record.AsMemory(HeaderBytes), token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(record.AsSpan(0, length - ChecksumBytes)), record.AsSpan(length - ChecksumBytes)))
            {
                throw new InvalidDataException("Journal checksum mismatch.");
            }

            Apply(record);
        }
        if (_file.Length + ReservedBytes > MaximumFileBytes)
        {
            throw new InvalidDataException("Journal exceeds reserved completion capacity.");
        }
    }

    private void Apply(byte[] record)
    {
        var state = (EntityDeliveryState)record[HeaderBytes];
        var id = new Guid(record.AsSpan(HeaderBytes + 1, IdentityBytes));
        ReadOnlySpan<byte> payload = record.AsSpan(HeaderBytes + 1 + IdentityBytes, record.Length - RecordOverhead);
        if (id == Guid.Empty)
        {
            throw new InvalidDataException("Invalid delivery identity.");
        }

        if (state == EntityDeliveryState.Pending)
        {
            if (payload.Length < LengthBytes)
            {
                throw new InvalidDataException("Missing envelope length.");
            }

            int size = BinaryPrimitives.ReadInt32LittleEndian(payload);
            if (size is < 1 or > MaximumEnvelopeBytes || size > payload.Length - LengthBytes ||
                payload.Length - LengthBytes - size > MaximumSourceContextBytes || _entries.Count >= MaximumDeliveries || _entries.ContainsKey(id))
            {
                throw new InvalidDataException("Invalid retained envelope.");
            }

            _entries.Add(id, new(payload.Slice(LengthBytes, size).ToArray(), payload[(LengthBytes + size)..].ToArray()));
            _order.Add(id);
        }
        else if (_entries.TryGetValue(id, out Entry? entry) && state == EntityDeliveryState.Delivered && entry.Receipt is null &&
            payload.Length is > 0 and <= MaximumReceiptBytes)
        {
            entry.Receipt = payload.ToArray();
        }
        else if (_entries.TryGetValue(id, out entry) && state == EntityDeliveryState.Acknowledged && entry.Receipt is not null &&
            !entry.Acknowledged && payload.IsEmpty)
        {
            entry.Acknowledged = true;
        }
        else
        {
            throw new InvalidDataException("Invalid journal state transition.");
        }
    }

    private void TruncateTail(long length)
    {
        _file.SetLength(length);
        _file.Position = length;
        _file.Flush(flushToDisk: true);
    }

    private void Observe(string stage)
    {
        try
        {
            Boundary?.Invoke(stage);
        }
        catch
        {
            _unusable = true;
            throw;
        }
    }

    private void CheckUsable()
    {
        if (_unusable)
        {
            throw new InvalidOperationException("The journal is closed or requires recovery.");
        }
    }

    /// <summary>Closes the journal and releases exclusive ownership after waiting for the current journal operation.</summary>
    /// <returns>A task that completes when the file and ownership handles are released.</returns>
    /// <remarks>Disposal does not deliver pending envelopes or acknowledge their sources. Retained records remain available when the journal is reopened.</remarks>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _unusable = true;
            try
            {
                await _file.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (_ownership is not null)
                {
                    await _ownership.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class Entry(byte[] envelope, byte[] context)
    {
        internal byte[] Envelope { get; } = envelope;

        internal byte[] Context { get; } = context;

        internal byte[]? Receipt
        {
            get; set;
        }

        internal bool Acknowledged
        {
            get; set;
        }
    }
}
