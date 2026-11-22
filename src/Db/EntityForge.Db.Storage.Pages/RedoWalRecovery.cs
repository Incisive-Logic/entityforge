using EntityForge.Db.Durability;
using System.Security.Cryptography;

namespace EntityForge.Db.Storage.Pages;

internal sealed record RedoWalRecoveryResult(IReadOnlyList<RedoWalTransaction> Transactions, long DurableLsn,
    long RequiredCheckpointLsn = 0);

internal sealed record RedoWalReclamationResult(int RemovedTransactions, long DurableLsn);

internal static class RedoWalRecovery
{
    private const int ReclamationBufferBytes = 64 * 1024;

    internal static async ValueTask<RedoWalRecoveryResult> ReadAsync(Stream stream, bool repairTail,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var committed = new List<RedoWalTransaction>();
        var pending = new List<PendingTransaction>();
        long groupStart = 0;
        long durableLsn = 0;
        long requiredCheckpointLsn = 0;
        byte[] header = new byte[RedoWalCodec.HeaderSize];
        while (true)
        {
            long recordStart = stream.Position;
            int headerBytes = await ReadAtMostAsync(stream, header, cancellationToken).ConfigureAwait(false);
            if (headerBytes == 0)
            {
                break;
            }

            if (headerBytes != header.Length)
            {
                if (repairTail)
                {
                    stream.SetLength(groupStart);
                    break;
                }
                throw new RedoWalCorruptionException("The redo log ends inside a record header.");
            }
            // Validate a complete header before treating a missing body as a repairable tail.
            RedoWalCodec.InspectHeader(header);
            int length = RedoWalCodec.ReadLength(header);
            if (length - header.Length > stream.Length - stream.Position)
            {
                if (repairTail)
                {
                    stream.SetLength(groupStart);
                    break;
                }
                throw new RedoWalCorruptionException("The redo log ends inside a record.");
            }

            byte[] record = new byte[length];
            header.CopyTo(record, 0);
            int remainder = await ReadAtMostAsync(stream, record.AsMemory(header.Length), cancellationToken)
                .ConfigureAwait(false);
            if (remainder != record.Length - header.Length)
            {
                if (repairTail)
                {
                    stream.SetLength(groupStart);
                    break;
                }
                throw new RedoWalCorruptionException("The redo log ends inside a record.");
            }
            DecodedRedoRecord decoded = RedoWalCodec.Decode(record);
            if (decoded.Kind == RedoWalCodec.TransactionRecord)
            {
                if (pending.Count == 0)
                {
                    groupStart = recordStart;
                }

                RedoTransactionFragment payload = RedoWalCodec.DecodeTransaction(decoded.Payload);
                if (pending.Count != 0 && pending[^1].Lsn == decoded.Lsn)
                {
                    pending[^1].Append(payload);
                }
                else
                {
                    if (decoded.Lsn <= (pending.Count == 0 ? durableLsn : pending[^1].Lsn))
                    {
                        throw new RedoWalCorruptionException("Redo transaction LSNs must increase.");
                    }

                    if (payload.EnvelopeLength > stream.Length)
                    {
                        throw new RedoWalCorruptionException("A redo envelope cannot exceed its containing log.");
                    }

                    long previousLsn = pending.Count == 0 ? durableLsn : pending[^1].Lsn;
                    if (decoded.Lsn - previousLsn > 1)
                    {
                        requiredCheckpointLsn = Math.Max(requiredCheckpointLsn, decoded.Lsn - 1);
                    }

                    pending.Add(new(decoded.Lsn, payload));
                }
            }
            else if (decoded.Kind == RedoWalCodec.GroupMarkerRecord)
            {
                int count = RedoWalCodec.DecodeGroupCount(decoded.Payload);
                if (count == 0)
                {
                    if (pending.Count != 0 || decoded.Lsn < durableLsn)
                    {
                        throw new RedoWalCorruptionException("A redo LSN-floor marker is out of sequence.");
                    }

                    requiredCheckpointLsn = Math.Max(requiredCheckpointLsn, decoded.Lsn);
                    durableLsn = decoded.Lsn;
                    groupStart = stream.Position;
                    continue;
                }
                if (count != pending.Count || pending.Count == 0 || decoded.Lsn != pending[^1].Lsn)
                {
                    throw new RedoWalCorruptionException("A committed redo group marker does not match its transactions.");
                }

                committed.AddRange(pending.Select(item => item.Build()));
                pending.Clear();
                durableLsn = decoded.Lsn;
                groupStart = stream.Position;
            }
            else
            {
                throw new RedoWalCorruptionException("A redo record kind is unsupported.");
            }
        }
        if (pending.Count != 0 && repairTail)
        {
            stream.SetLength(groupStart);
        }
        else if (pending.Count != 0)
        {
            throw new RedoWalCorruptionException("The redo log contains an uncommitted tail.");
        }

        return new(committed, durableLsn, requiredCheckpointLsn);
    }

    internal static async ValueTask<RedoWalReclamationResult> RewriteAfterAsync(Stream source,
        Stream target, DatabaseVersion checkpointVersion, CancellationToken cancellationToken, long? checkpointPosition = null,
        long initialDurableLsn = 0)
    {
        source.Position = 0;
        int removed = 0;
        int pendingCount = 0;
        int retainedCount = 0;
        long pendingLsn = 0;
        long retainedLsn = 0;
        long durableLsn = initialDurableLsn;
        long? deferredFloorLsn = null;
        byte[] header = new byte[RedoWalCodec.HeaderSize];
        byte[] buffer = new byte[ReclamationBufferBytes];
        byte[] prefix = new byte[RedoWalCodec.TransactionPrefixLength];
        byte[] checksum = new byte[RedoWalCodec.ChecksumSize];
        byte[] actualChecksum = new byte[RedoWalCodec.ChecksumSize];
        byte[] marker = new byte[RedoWalCodec.HeaderSize + sizeof(int) + RedoWalCodec.ChecksumSize];
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (true)
        {
            int headerBytes = await ReadAtMostAsync(source, header, cancellationToken).ConfigureAwait(false);
            if (headerBytes == 0)
            {
                break;
            }

            if (headerBytes != header.Length)
            {
                throw new RedoWalCorruptionException("The redo log ends inside a record header.");
            }

            RedoRecordMetadata metadata = RedoWalCodec.InspectHeader(header);
            if (metadata.Kind == RedoWalCodec.TransactionRecord)
            {
                if (metadata.PayloadLength < RedoWalCodec.TransactionPrefixLength)
                {
                    throw new RedoWalCorruptionException("A redo transaction prefix is truncated.");
                }

                if (await ReadAtMostAsync(source, prefix, cancellationToken).ConfigureAwait(false) != prefix.Length)
                {
                    throw new RedoWalCorruptionException("The redo log ends inside a transaction prefix.");
                }

                bool retain = RedoWalCodec.DecodeDatabaseVersion(prefix).Value > checkpointVersion.Value &&
                    (checkpointPosition is null || metadata.Lsn > checkpointPosition.Value);
                hash.AppendData(header);
                hash.AppendData(prefix);
                if (retain)
                {
                    if (deferredFloorLsn is long floorLsn)
                    {
                        await target.WriteAsync(RedoWalCodec.GroupMarker(floorLsn, 0),
                            cancellationToken).ConfigureAwait(false);
                        deferredFloorLsn = null;
                    }
                    await target.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                    await target.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
                }
                int remaining = metadata.PayloadLength - prefix.Length;
                while (remaining != 0)
                {
                    int requested = Math.Min(buffer.Length, remaining);
                    int read = await ReadAtMostAsync(source, buffer.AsMemory(0, requested), cancellationToken)
                        .ConfigureAwait(false);
                    if (read != requested)
                    {
                        throw new RedoWalCorruptionException("The redo log ends inside a transaction record.");
                    }

                    hash.AppendData(buffer.AsSpan(0, read));
                    if (retain)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }

                    remaining -= read;
                }
                if (await ReadAtMostAsync(source, checksum, cancellationToken).ConfigureAwait(false) != checksum.Length)
                {
                    throw new RedoWalCorruptionException("The redo log ends inside a transaction checksum.");
                }

                if (!hash.TryGetHashAndReset(actualChecksum, out int hashedBytes) ||
                    hashedBytes != actualChecksum.Length || !actualChecksum.AsSpan().SequenceEqual(checksum))
                {
                    throw new RedoWalCorruptionException("A redo record checksum is invalid.");
                }

                if (retain)
                {
                    await target.WriteAsync(checksum, cancellationToken).ConfigureAwait(false);
                }

                bool startsTransaction = pendingCount == 0 || pendingLsn != metadata.Lsn;
                if (startsTransaction)
                {
                    if (metadata.Lsn <= (pendingCount == 0 ? durableLsn : pendingLsn))
                    {
                        throw new RedoWalCorruptionException("Redo transaction LSNs must increase.");
                    }

                    pendingCount++;
                }
                pendingLsn = metadata.Lsn;
                if (retain && (retainedCount == 0 || retainedLsn != metadata.Lsn))
                {
                    retainedCount++;
                }

                if (retain)
                {
                    retainedLsn = metadata.Lsn;
                }

                continue;
            }
            if (metadata.Kind != RedoWalCodec.GroupMarkerRecord)
            {
                throw new RedoWalCorruptionException("A redo record kind is unsupported.");
            }

            if (metadata.PayloadLength != sizeof(int))
            {
                throw new RedoWalCorruptionException("A redo group marker payload has invalid bounds.");
            }

            header.CopyTo(marker, 0);
            if (await ReadAtMostAsync(source, marker.AsMemory(header.Length), cancellationToken).ConfigureAwait(false) !=
                marker.Length - header.Length)
            {
                throw new RedoWalCorruptionException("The redo log ends inside a group marker.");
            }

            RedoWalCodec.Inspect(marker);
            int declaredCount = RedoWalCodec.DecodeGroupCount(marker.AsSpan(RedoWalCodec.HeaderSize, sizeof(int)));
            if (declaredCount == 0)
            {
                if (pendingCount != 0 || metadata.Lsn < durableLsn)
                {
                    throw new RedoWalCorruptionException("A redo LSN-floor marker appears inside a transaction group.");
                }

                durableLsn = metadata.Lsn;
                deferredFloorLsn = metadata.Lsn;
                continue;
            }
            if (pendingCount == 0 || declaredCount != pendingCount || metadata.Lsn != pendingLsn)
            {
                throw new RedoWalCorruptionException("A committed redo group marker does not match its transactions.");
            }

            removed += pendingCount - retainedCount;
            durableLsn = metadata.Lsn;
            if (retainedCount != 0)
            {
                await target.WriteAsync(RedoWalCodec.GroupMarker(retainedLsn, retainedCount),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                deferredFloorLsn = metadata.Lsn;
            }

            pendingCount = 0;
            retainedCount = 0;
            pendingLsn = 0;
            retainedLsn = 0;
        }
        if (pendingCount != 0)
        {
            throw new RedoWalCorruptionException("The redo log contains an uncommitted tail.");
        }

        if (deferredFloorLsn is long finalFloorLsn)
        {
            await target.WriteAsync(RedoWalCodec.GroupMarker(finalFloorLsn, 0),
                cancellationToken).ConfigureAwait(false);
        }

        return new(removed, durableLsn);
    }

    private static async ValueTask<int> ReadAtMostAsync(Stream stream, Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }
        return total;
    }

    private sealed class PendingTransaction
    {
        private readonly DatabaseVersion _version;
        private readonly byte[] _envelope;
        private readonly List<RedoPageEntry> _entries;
        private int _envelopeBytes;

        internal PendingTransaction(long lsn, RedoTransactionFragment fragment)
        {
            Lsn = lsn;
            _version = fragment.Version;
            _envelope = GC.AllocateUninitializedArray<byte>(fragment.EnvelopeLength);
            _entries = new(fragment.PageEntries.Count);
            Append(fragment);
        }

        internal long Lsn
        {
            get;
        }

        internal void Append(RedoTransactionFragment fragment)
        {
            if (fragment.Version != _version || fragment.EnvelopeLength != _envelope.Length ||
                fragment.EnvelopeOffset != _envelopeBytes)
            {
                throw new RedoWalCorruptionException("Redo transaction envelope fragments are not contiguous.");
            }

            fragment.EnvelopeBytes.Span.CopyTo(_envelope.AsSpan(_envelopeBytes));
            _envelopeBytes += fragment.EnvelopeBytes.Length;
            _entries.AddRange(fragment.PageEntries);
        }

        internal RedoWalTransaction Build()
        {
            if (_envelopeBytes != _envelope.Length)
            {
                throw new RedoWalCorruptionException("A redo transaction envelope is incomplete.");
            }

            DurableTransactionEnvelope transaction =
                RedoWalCodec.DecodeTransactionEnvelope(_envelope, _version);
            return new(Lsn, transaction, _entries);
        }
    }
}
