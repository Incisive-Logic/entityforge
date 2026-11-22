using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.Durability;

namespace EntityForge.Db.Storage.Pages;

internal static class RedoWalCodec
{
    internal const int HeaderSize = 24;
    internal const int ChecksumSize = 32;
    internal const byte FormatVersion = 9;
    internal const byte TransactionRecord = 1;
    internal const byte GroupMarkerRecord = 2;
    internal const int MaximumRecordBytes = 256 * 1024 * 1024;
    internal const int MaximumTransactionRecordBytes = 128 * 1024 * 1024;
    private const uint Magic = 0x57524645;
    private const int IntegerLength = sizeof(int);
    internal const int TransactionPrefixLength = sizeof(long);
    internal const int TransactionFragmentPrefixLength = sizeof(long) + (3 * IntegerLength);
    private const int EntryHeaderLength = sizeof(byte) + sizeof(long) + sizeof(int) + sizeof(int);
    private const int GuidLength = 16;
    private const int CompressionLengthPrefix = sizeof(int);
    private const int MinimumCompressionSaving = 64;
    private const int MaximumBufferedFragmentBytes = 64 * 1024;
    private const int MaximumPreparedPageEntries = 256;
    private const int MaximumStringBytes = 16 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Transaction(long lsn, DurableTransactionEnvelope transaction,
        IReadOnlyList<RedoPageEntry>? pageEntries = null)
    {
        using var prepared = PrepareEntries(pageEntries ?? Array.Empty<RedoPageEntry>());
        IReadOnlyList<RedoPageEntry> entries = prepared.Entries;
        byte[] transactionBytes = EncodeTransactionEnvelope(transaction);
        byte[] payload = TransactionPayload(transaction.Version, transactionBytes, entries);
        return Record(TransactionRecord, lsn, payload);
    }

    internal static async ValueTask<long> WriteTransactionAsync(Stream stream, long lsn,
        DurableTransactionEnvelope transaction, IReadOnlyList<RedoPageEntry> pageEntries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        int length = TransactionEnvelopeLength(transaction);
        bool pooled = length <= MaximumBufferedFragmentBytes;
        byte[] buffer = pooled ? ArrayPool<byte>.Shared.Rent(length) : new byte[length];
        try
        {
            EncodeTransactionEnvelope(transaction, buffer.AsSpan(0, length));
            return await WritePreparedTransactionAsync(stream, lsn, transaction.Version,
                buffer.AsMemory(0, length), pageEntries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (pooled)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static async ValueTask<long> WritePreparedTransactionAsync(Stream stream, long lsn,
        DatabaseVersion version, ReadOnlyMemory<byte> envelope, IReadOnlyList<RedoPageEntry> pageEntries,
        CancellationToken cancellationToken)
    {
        int maximumPayloadLength = MaximumTransactionRecordBytes - HeaderSize - ChecksumSize;
        long written = 0;
        int envelopeOffset = 0;
        int start = 0;
        int maximumFragmentBytes = maximumPayloadLength - TransactionFragmentPrefixLength - IntegerLength;
        while (envelopeOffset < envelope.Length || start < pageEntries.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Retain compressed copies only for this batch, including while its writes await I/O.
            // The original page buffers remain caller-owned; fragmentation preserves one logical transaction.
            int batchCount = Math.Min(MaximumPreparedPageEntries, pageEntries.Count - start);
            var batch = new RedoPageEntry[batchCount];
            for (int index = 0; index < batchCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch[index] = pageEntries[start + index];
            }

            using var prepared = PrepareEntries(batch, cancellationToken);
            int batchOffset = 0;
            do
            {
                int fragmentLength = Math.Min(maximumFragmentBytes, envelope.Length - envelopeOffset);
                int count = 0;
                int payloadLength = TransactionFragmentPrefixLength + fragmentLength + IntegerLength;
                while (batchOffset + count < batchCount)
                {
                    RedoPageEntry entry = prepared.Entries[batchOffset + count];
                    int candidate = checked(payloadLength + EntryHeaderLength + entry.Bytes.Length);
                    if (candidate > maximumPayloadLength)
                    {
                        break;
                    }

                    payloadLength = candidate;
                    count++;
                }
                if (count == 0 && fragmentLength == 0)
                {
                    throw new PhysicalPageCapacityException("A redo page entry exceeds the supported segment size.");
                }

                written += await WriteTransactionFragmentAsync(stream, lsn, version,
                    envelope.Length, envelopeOffset, envelope.Slice(envelopeOffset, fragmentLength),
                    prepared.Entries, batchOffset, count, cancellationToken).ConfigureAwait(false);
                envelopeOffset += fragmentLength;
                batchOffset += count;
            } while (batchOffset < batchCount);
            start += batchCount;
        }
        return written;
    }

    private static async ValueTask<int> WriteTransactionFragmentAsync(Stream stream, long lsn,
        DatabaseVersion version, int envelopeLength, int envelopeOffset, ReadOnlyMemory<byte> envelopeBytes,
        IReadOnlyList<RedoPageEntry> pageEntries, int start, int count,
        CancellationToken cancellationToken)
    {
        int payloadLength = checked(TransactionFragmentPrefixLength + envelopeBytes.Length + IntegerLength +
            PageEntriesLength(pageEntries, start, count));
        int recordLength = RecordLength(payloadLength);
        if (recordLength <= MaximumBufferedFragmentBytes)
        {
            // Small logical/page records need one hash and one write; larger fragments keep
            // bounded streaming. The bytes and checksum are identical to the streamed frame.
            byte[] buffer = ArrayPool<byte>.Shared.Rent(recordLength);
            try
            {
                WriteHeader(buffer.AsSpan(0, HeaderSize), TransactionRecord, lsn, payloadLength, recordLength);
                EncodeBufferedFragment(buffer, version, envelopeLength, envelopeOffset,
                    envelopeBytes.Span, pageEntries, start, count);
                await stream.WriteAsync(buffer.AsMemory(0, recordLength), cancellationToken).ConfigureAwait(false);
                return recordLength;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        byte[] header = Header(TransactionRecord, lsn, payloadLength, recordLength);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await WriteHashedAsync(stream, header, hash, cancellationToken).ConfigureAwait(false);
        byte[] integer = new byte[IntegerLength];
        byte[] versionBytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(versionBytes, version.Value);
        await WriteHashedAsync(stream, versionBytes, hash, cancellationToken).ConfigureAwait(false);
        BinaryPrimitives.WriteInt32LittleEndian(integer, envelopeLength);
        await WriteHashedAsync(stream, integer, hash, cancellationToken).ConfigureAwait(false);
        BinaryPrimitives.WriteInt32LittleEndian(integer, envelopeOffset);
        await WriteHashedAsync(stream, integer, hash, cancellationToken).ConfigureAwait(false);
        BinaryPrimitives.WriteInt32LittleEndian(integer, envelopeBytes.Length);
        await WriteHashedAsync(stream, integer, hash, cancellationToken).ConfigureAwait(false);
        await WriteHashedAsync(stream, envelopeBytes, hash, cancellationToken).ConfigureAwait(false);
        BinaryPrimitives.WriteInt32LittleEndian(integer, count);
        await WriteHashedAsync(stream, integer, hash, cancellationToken).ConfigureAwait(false);
        byte[] entryHeader = new byte[EntryHeaderLength];
        for (int index = start; index < start + count; index++)
        {
            RedoPageEntry entry = pageEntries[index];
            entryHeader[0] = (byte)entry.Kind;
            BinaryPrimitives.WriteInt64LittleEndian(entryHeader.AsSpan(sizeof(byte)), entry.PageId);
            BinaryPrimitives.WriteInt32LittleEndian(entryHeader.AsSpan(sizeof(byte) + sizeof(long)), entry.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(entryHeader.AsSpan(sizeof(byte) + sizeof(long) + sizeof(int)),
                entry.Bytes.Length);
            await WriteHashedAsync(stream, entryHeader, hash, cancellationToken).ConfigureAwait(false);
            await WriteHashedAsync(stream, entry.Bytes, hash, cancellationToken).ConfigureAwait(false);
        }
        await stream.WriteAsync(hash.GetHashAndReset(), cancellationToken).ConfigureAwait(false);
        return recordLength;
    }

    private static void EncodeBufferedFragment(Span<byte> buffer, DatabaseVersion version,
        int envelopeLength, int envelopeOffset, ReadOnlySpan<byte> envelopeBytes,
        IReadOnlyList<RedoPageEntry> entries, int start, int count)
    {
        int cursor = HeaderSize;
        WriteInt64(buffer, ref cursor, version.Value);
        WriteInt32(buffer, ref cursor, envelopeLength);
        WriteInt32(buffer, ref cursor, envelopeOffset);
        WriteInt32(buffer, ref cursor, envelopeBytes.Length);
        envelopeBytes.CopyTo(buffer[cursor..]);
        cursor += envelopeBytes.Length;
        WriteInt32(buffer, ref cursor, count);
        for (int index = start; index < start + count; index++)
        {
            RedoPageEntry entry = entries[index];
            buffer[cursor++] = (byte)entry.Kind;
            WriteInt64(buffer, ref cursor, entry.PageId);
            WriteInt32(buffer, ref cursor, entry.Offset);
            WriteInt32(buffer, ref cursor, entry.Bytes.Length);
            entry.Bytes.Span.CopyTo(buffer[cursor..]);
            cursor += entry.Bytes.Length;
        }
        SHA256.HashData(buffer[..cursor], buffer.Slice(cursor, ChecksumSize));
    }

    private static int PageEntriesLength(IReadOnlyList<RedoPageEntry> entries, int start, int count)
    {
        int length = 0;
        for (int index = start; index < start + count; index++)
        {
            length = checked(length + EntryHeaderLength + entries[index].Bytes.Length);
        }

        return length;
    }

    internal static byte[] GroupMarker(long lastLsn, int transactionCount)
    {
        byte[] payload = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(payload, transactionCount);
        return Record(GroupMarkerRecord, lastLsn, payload);
    }

    internal static DecodedRedoRecord Decode(ReadOnlySpan<byte> record)
    {
        RedoRecordMetadata metadata = Inspect(record);
        return new(metadata.Kind, metadata.Lsn,
            record.Slice(HeaderSize, metadata.PayloadLength).ToArray());
    }

    internal static RedoRecordMetadata Inspect(ReadOnlySpan<byte> record)
    {
        if (record.Length < HeaderSize + ChecksumSize)
        {
            throw new RedoWalCorruptionException("A redo record is shorter than its envelope.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Magic)
        {
            throw new RedoWalCorruptionException("A redo record has invalid magic.");
        }

        if (record[4] != FormatVersion)
        {
            throw new RedoWalCorruptionException("A redo record has an unsupported version.");
        }

        byte kind = record[5];
        int length = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
        if (length != record.Length || length > MaximumRecordBytes)
        {
            throw new RedoWalCorruptionException("A redo record has an invalid length.");
        }

        long lsn = BinaryPrimitives.ReadInt64LittleEndian(record[12..]);
        int payloadLength = length - HeaderSize - ChecksumSize;
        uint declaredPayloadLength = BinaryPrimitives.ReadUInt32LittleEndian(record[20..]);
        if (declaredPayloadLength != payloadLength)
        {
            throw new RedoWalCorruptionException("A redo record payload length is invalid.");
        }

        Span<byte> actual = stackalloc byte[ChecksumSize];
        SHA256.HashData(record[..^ChecksumSize], actual);
        if (!actual.SequenceEqual(record[^ChecksumSize..]))
        {
            throw new RedoWalCorruptionException("A redo record checksum is invalid.");
        }

        return new(kind, lsn, payloadLength);
    }

    internal static RedoRecordMetadata InspectHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic ||
            header[4] != FormatVersion)
        {
            throw new RedoWalCorruptionException("A redo record header is invalid.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        if (length < HeaderSize + ChecksumSize || length > MaximumRecordBytes ||
            payloadLength != length - HeaderSize - ChecksumSize)
        {
            throw new RedoWalCorruptionException("A redo record header has invalid bounds.");
        }

        return new(header[5], BinaryPrimitives.ReadInt64LittleEndian(header[12..]), (int)payloadLength);
    }

    internal static DurableTransactionEnvelope DecodeTransactionEnvelope(
        ReadOnlySpan<byte> envelope, DatabaseVersion declaredVersion)
    {
        DurableTransactionEnvelope transaction;
        try
        {
            transaction = DecodeBinaryTransactionEnvelope(envelope);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or DecoderFallbackException or InvalidDataException)
        {
            throw new RedoWalCorruptionException("A redo transaction envelope is invalid.", exception);
        }
        if (transaction.Version != declaredVersion)
        {
            throw new RedoWalCorruptionException("A redo transaction version does not match its envelope.");
        }

        return transaction;
    }

    internal static DatabaseVersion DecodeDatabaseVersion(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < TransactionPrefixLength)
        {
            throw new RedoWalCorruptionException("A redo transaction prefix is truncated.");
        }

        return new(BinaryPrimitives.ReadInt64LittleEndian(payload));
    }

    internal static RedoTransactionFragment DecodeTransaction(ReadOnlySpan<byte> payload)
    {
        DatabaseVersion version = DecodeDatabaseVersion(payload);
        int cursor = sizeof(long);
        int envelopeLength = ReadInteger(payload, ref cursor, "transaction envelope length");
        int envelopeOffset = ReadInteger(payload, ref cursor, "transaction envelope offset");
        int fragmentLength = ReadInteger(payload, ref cursor, "transaction envelope fragment length");
        if (envelopeLength <= 0 || envelopeOffset < 0 || fragmentLength < 0 ||
            envelopeOffset > envelopeLength || fragmentLength > envelopeLength - envelopeOffset ||
            fragmentLength > payload.Length - cursor)
        {
            throw new RedoWalCorruptionException("A redo transaction envelope fragment has invalid bounds.");
        }

        byte[] envelopeBytes = payload.Slice(cursor, fragmentLength).ToArray();
        cursor += fragmentLength;
        int entryCount = ReadInteger(payload, ref cursor, "page-entry count");
        if (entryCount < 0 || entryCount > (payload.Length - cursor) / EntryHeaderLength)
        {
            throw new RedoWalCorruptionException("A redo page-entry count is invalid.");
        }

        var entries = new RedoPageEntry[entryCount];
        for (int index = 0; index < entryCount; index++)
        {
            if (cursor + EntryHeaderLength > payload.Length)
            {
                throw new RedoWalCorruptionException("A redo page entry is truncated.");
            }

            RedoPageEntryKind kind = (RedoPageEntryKind)payload[cursor];
            long pageId = BinaryPrimitives.ReadInt64LittleEndian(payload[(cursor + sizeof(byte))..]);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(payload[(cursor + sizeof(byte) + sizeof(long))..]);
            int bytesLength = BinaryPrimitives.ReadInt32LittleEndian(
                payload[(cursor + sizeof(byte) + sizeof(long) + sizeof(int))..]);
            cursor += EntryHeaderLength;
            if (!Enum.IsDefined(kind) || pageId < 0 || offset < 0 || bytesLength < 0 ||
                bytesLength > payload.Length - cursor)
            {
                throw new RedoWalCorruptionException("A redo page entry has invalid bounds.");
            }

            ReadOnlySpan<byte> encodedBytes = payload.Slice(cursor, bytesLength);
            entries[index] = DecodeEntry(kind, pageId, offset, encodedBytes);
            cursor += bytesLength;
        }
        if (cursor != payload.Length)
        {
            throw new RedoWalCorruptionException("A redo transaction payload has trailing bytes.");
        }

        return new(version, envelopeLength, envelopeOffset, envelopeBytes, entries);
    }

    internal static int DecodeGroupCount(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != sizeof(int))
        {
            throw new RedoWalCorruptionException("A redo group marker is invalid.");
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(payload);
        // A zero-count marker retains the durable LSN floor after checkpoint reclamation
        // removes every transaction from a group. Without it, reopening an empty tail
        // reuses old LSNs and page recovery can mistake new redo for already-applied work.
        if (count < 0)
        {
            throw new RedoWalCorruptionException("A redo group marker count is invalid.");
        }

        return count;
    }

    internal static int ReadLength(ReadOnlySpan<byte> header)
    {
        if (header.Length != HeaderSize)
        {
            throw new ArgumentException("A complete redo header is required.", nameof(header));
        }

        return BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
    }

    private static int TransactionEnvelopeLength(DurableTransactionEnvelope transaction)
    {
        int length = checked((transaction.Timestamp is null ? 0 : DurableTimestampState.EncodedLength) + DatabaseVersioningFormat.Length(transaction.VersioningMode) + DatabaseStorageLayout.EncodedLength + GuidLength + sizeof(long) + GuidLength + sizeof(byte) +
            StringLength(transaction.Payload.CodecId) + sizeof(int) + sizeof(int) +
            transaction.Payload.Content.Length + sizeof(byte));
        if (transaction.CommittedChanges is not CommittedChangePayload changes)
        {
            return length;
        }

        length = checked(length + sizeof(byte) + StringLength(changes.CodecId) + sizeof(int) + sizeof(int) +
            changes.Content.Length + sizeof(int) + sizeof(int) + sizeof(int));
        return checked(length + changes.Routing.Keys.Sum(key =>
            StringLength(key.Kind) + StringLength(key.Value)) +
            changes.Routing.OverflowedKinds.Sum(StringLength));
    }

    private static byte[] EncodeTransactionEnvelope(DurableTransactionEnvelope transaction)
    {
        byte[] result = new byte[TransactionEnvelopeLength(transaction)];
        EncodeTransactionEnvelope(transaction, result);
        return result;
    }

    private static void EncodeTransactionEnvelope(DurableTransactionEnvelope transaction, Span<byte> result)
    {
        int cursor = 0;
        result[cursor++] = (byte)transaction.StorageLayout.Kind;
        WriteInt32(result, ref cursor, transaction.StorageLayout.Version);
        WriteGuid(result, ref cursor, transaction.DatabaseIncarnation);
        WriteInt64(result, ref cursor, transaction.Version.Value);
        WriteGuid(result, ref cursor, transaction.TransactionId);
        result[cursor++] = (byte)transaction.Payload.Profile;
        WriteString(result, ref cursor, transaction.Payload.CodecId);
        WriteInt32(result, ref cursor, transaction.Payload.CodecVersion);
        WriteMemory(result, ref cursor, transaction.Payload.Content);
        result[cursor++] = (byte)transaction.ChangeKind;
        if (transaction.CommittedChanges is CommittedChangePayload changes)
        {
            result[cursor++] = (byte)changes.Kind;
            WriteString(result, ref cursor, changes.CodecId);
            WriteInt32(result, ref cursor, changes.CodecVersion);
            WriteMemory(result, ref cursor, changes.Content);
            WriteInt32(result, ref cursor, changes.EffectCount);
            WriteInt32(result, ref cursor, changes.Routing.Keys.Count);
            foreach (CommittedChangeRoutingKey key in changes.Routing.Keys)
            {
                WriteString(result, ref cursor, key.Kind);
                WriteString(result, ref cursor, key.Value);
            }
            WriteInt32(result, ref cursor, changes.Routing.OverflowedKinds.Count);
            foreach (string kind in changes.Routing.OverflowedKinds)
            {
                WriteString(result, ref cursor, kind);
            }
        }
        if (transaction.Timestamp is { } timestamp)
        {
            timestamp.Encode().AsSpan().CopyTo(result[cursor..]);
            cursor += DurableTimestampState.EncodedLength;
        }
        DatabaseVersioningFormat.Write(result[cursor..], transaction.VersioningMode);
    }

    private static DurableTransactionEnvelope DecodeBinaryTransactionEnvelope(ReadOnlySpan<byte> payload)
    {
        int cursor = 0;
        var storageLayout = new DatabaseStorageLayout((DatabaseStorageLayoutKind)ReadByte(payload, ref cursor, "storage layout"),
            ReadInteger(payload, ref cursor, "storage layout version"));
        Guid databaseIncarnation = ReadGuid(payload, ref cursor);
        var version = new DatabaseVersion(ReadInt64(payload, ref cursor, "transaction version"));
        Guid transactionId = ReadGuid(payload, ref cursor);
        DatabaseProfileId profile = (DatabaseProfileId)ReadByte(payload, ref cursor, "profile");
        string profileCodec = ReadString(payload, ref cursor);
        int profileCodecVersion = ReadInteger(payload, ref cursor, "profile codec version");
        byte[] profileContent = ReadMemory(payload, ref cursor, "profile payload");
        DurableCommittedChangeKind changeKind =
            (DurableCommittedChangeKind)ReadByte(payload, ref cursor, "change kind");
        CommittedChangePayload? changes = null;
        if (changeKind == DurableCommittedChangeKind.UserDataWithChanges)
        {
            var payloadKind = (CommittedChangeKind)ReadByte(payload, ref cursor, "change payload kind");
            if (!Enum.IsDefined(payloadKind))
            {
                throw new RedoWalCorruptionException("Invalid change payload kind.");
            }

            string codec = ReadString(payload, ref cursor);
            int codecVersion = ReadInteger(payload, ref cursor, "change codec version");
            byte[] content = ReadMemory(payload, ref cursor, "committed changes");
            int effectCount = ReadInteger(payload, ref cursor, "effect count");
            int routingCount = ReadInteger(payload, ref cursor, "routing-key count");
            if (routingCount < 0 || routingCount > CommittedChangeRoutingSummary.MaximumRoutingKeys)
            {
                throw new RedoWalCorruptionException("A redo routing-key count is invalid.");
            }

            var routing = new CommittedChangeRoutingKey[routingCount];
            for (int index = 0; index < routingCount; index++)
            {
                routing[index] = new(ReadString(payload, ref cursor), ReadString(payload, ref cursor));
            }

            int overflowCount = ReadInteger(payload, ref cursor, "overflowed routing-kind count");
            if (overflowCount < 0 || overflowCount > CommittedChangeRoutingSummary.MaximumOverflowedKinds)
            {
                throw new RedoWalCorruptionException("A redo overflowed routing-kind count is invalid.");
            }

            var overflowedKinds = new string[overflowCount];
            for (int index = 0; index < overflowCount; index++)
            {
                overflowedKinds[index] = ReadString(payload, ref cursor);
            }

            changes = new(codec, codecVersion, content, effectCount,
                new(routing, overflowedKinds), payloadKind);
        }
        DurableTimestampState? timestamp = null;
        if (DurableTimestampState.HasMarker(payload[cursor..]))
        {
            if (payload.Length - cursor < DurableTimestampState.EncodedLength)
            {
                throw new InvalidDataException("Truncated timestamp.");
            }

            timestamp = DurableTimestampState.Decode(payload.Slice(cursor, DurableTimestampState.EncodedLength));
            cursor += DurableTimestampState.EncodedLength;
        }
        var versioningMode = DatabaseVersioningFormat.Read(payload[cursor..]);
        return new(databaseIncarnation, version, transactionId,
            new(profile, profileCodec, profileCodecVersion, profileContent), changeKind, changes, storageLayout, versioningMode)
        {
            Timestamp = timestamp
        };
    }

    private static byte[] Record(byte kind, long lsn, byte[] payload)
    {
        int length = RecordLength(payload.Length);
        byte[] record = new byte[length];
        WriteHeader(record.AsSpan(0, HeaderSize), kind, lsn, payload.Length, length);
        payload.CopyTo(record, HeaderSize);
        SHA256.HashData(record.AsSpan(0, length - ChecksumSize)).CopyTo(record, length - ChecksumSize);
        return record;
    }

    private static byte[] TransactionPayload(DatabaseVersion version, byte[] transaction,
        IReadOnlyList<RedoPageEntry> entries)
    {
        byte[] payload = new byte[PayloadLength(transaction.Length, entries)];
        int cursor = 0;
        BinaryPrimitives.WriteInt64LittleEndian(payload, version.Value);
        cursor += sizeof(long);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(cursor), transaction.Length);
        cursor += IntegerLength;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(cursor), 0);
        cursor += IntegerLength;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(cursor), transaction.Length);
        cursor += IntegerLength;
        transaction.CopyTo(payload, cursor);
        cursor += transaction.Length;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(cursor), entries.Count);
        cursor += IntegerLength;
        foreach (RedoPageEntry entry in entries)
        {
            payload[cursor] = (byte)entry.Kind;
            cursor += sizeof(byte);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(cursor), entry.PageId);
            cursor += sizeof(long);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(cursor), entry.Offset);
            cursor += sizeof(int);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(cursor), entry.Bytes.Length);
            cursor += sizeof(int);
            entry.Bytes.Span.CopyTo(payload.AsSpan(cursor));
            cursor += entry.Bytes.Length;
        }
        return payload;
    }

    private static int PayloadLength(int transactionLength, IReadOnlyList<RedoPageEntry> entries) => checked(
        TransactionFragmentPrefixLength + transactionLength + IntegerLength +
        entries.Sum(entry => EntryHeaderLength + entry.Bytes.Length));

    private static PreparedEntries PrepareEntries(IReadOnlyList<RedoPageEntry> entries,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entries.Count == 0)
        {
            return new(entries, null);
        }

        var result = new RedoPageEntry[entries.Count];
        byte[]? arena = null;
        int used = 0;
        try
        {
            for (int index = 0; index < entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RedoPageEntry entry = entries[index];
                // Even an empty compressed stream needs the length prefix. These
                // entries cannot meet the existing minimum saving, so retain bytes.
                if (entry.Bytes.Length < MinimumCompressionSaving + CompressionLengthPrefix)
                {
                    result[index] = entry;
                    continue;
                }
                int maximumLength = BrotliEncoder.GetMaxCompressedLength(entry.Bytes.Length);
                byte[] candidate = ArrayPool<byte>.Shared.Rent(checked(CompressionLengthPrefix + maximumLength));
                try
                {
                    BinaryPrimitives.WriteInt32LittleEndian(candidate, entry.Bytes.Length);
                    if (BrotliEncoder.TryCompress(entry.Bytes.Span, candidate.AsSpan(CompressionLengthPrefix,
                            maximumLength), out int compressedLength, quality: 1, window: 22) &&
                        compressedLength + CompressionLengthPrefix <= entry.Bytes.Length - MinimumCompressionSaving)
                    {
                        RedoPageEntryKind kind = entry.Kind switch
                        {
                            RedoPageEntryKind.FullPageProtection => RedoPageEntryKind.CompressedFullPageProtection,
                            RedoPageEntryKind.Patch => RedoPageEntryKind.CompressedPatch,
                            _ => throw new RedoWalCorruptionException("An in-memory redo page-entry kind is invalid.")
                        };
                        int length = CompressionLengthPrefix + compressedLength;
                        ReadOnlyMemory<byte> bytes;
                        // One bounded arena per transaction, never one lease per page.
                        // Overflow retains exact owned arrays instead of growing pooled retention.
                        if (length <= MaximumBufferedFragmentBytes - used)
                        {
                            arena ??= ArrayPool<byte>.Shared.Rent(MaximumBufferedFragmentBytes);
                            candidate.AsSpan(0, length).CopyTo(arena.AsSpan(used, length));
                            bytes = arena.AsMemory(used, length);
                            used += length;
                        }
                        else
                        {
                            bytes = candidate.AsMemory(0, length).ToArray();
                        }

                        result[index] = new(kind, entry.PageId, entry.Offset, bytes);
                    }
                    else
                    {
                        result[index] = entry;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(candidate);
                }
            }
            return new(result, arena);
        }
        catch
        {
            if (arena is not null)
            {
                ArrayPool<byte>.Shared.Return(arena);
            }

            throw;
        }
    }

    private readonly record struct PreparedEntries(IReadOnlyList<RedoPageEntry> Entries, byte[]? Arena) : IDisposable
    {
        // The complete asynchronous write owns this lease, including streamed fragments.
        public void Dispose()
        {
            if (Arena is not null)
            {
                ArrayPool<byte>.Shared.Return(Arena);
            }
        }
    }

    private static RedoPageEntry DecodeEntry(RedoPageEntryKind kind, long pageId, int offset,
        ReadOnlySpan<byte> bytes)
    {
        if (kind is RedoPageEntryKind.FullPageProtection or RedoPageEntryKind.Patch)
        {
            return new(kind, pageId, offset, bytes.ToArray());
        }

        if (bytes.Length < CompressionLengthPrefix)
        {
            throw new RedoWalCorruptionException("A compressed redo page entry is truncated.");
        }

        int decodedLength = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (decodedLength <= 0 || decodedLength > PhysicalPageFileOptions.DefaultPageSize)
        {
            throw new RedoWalCorruptionException("A compressed redo page entry has invalid bounds.");
        }

        byte[] decoded = new byte[decodedLength];
        using var decoder = new BrotliDecoder();
        ReadOnlySpan<byte> compressed = bytes[CompressionLengthPrefix..];
        if (decoder.Decompress(compressed, decoded, out int consumed, out int written) != OperationStatus.Done ||
            consumed != compressed.Length || written != decodedLength)
        {
            throw new RedoWalCorruptionException("A compressed redo page entry is invalid.");
        }

        RedoPageEntryKind decodedKind = kind switch
        {
            RedoPageEntryKind.CompressedFullPageProtection => RedoPageEntryKind.FullPageProtection,
            RedoPageEntryKind.CompressedPatch => RedoPageEntryKind.Patch,
            _ => throw new RedoWalCorruptionException("A redo page-entry kind is invalid.")
        };
        return new(decodedKind, pageId, offset, decoded);
    }

    private static int RecordLength(int payloadLength)
    {
        int length = checked(HeaderSize + payloadLength + ChecksumSize);
        if (length > MaximumRecordBytes)
        {
            throw new PhysicalPageCapacityException("A redo record exceeds the supported size.");
        }

        return length;
    }

    private static byte[] Header(byte kind, long lsn, int payloadLength, int recordLength)
    {
        byte[] header = new byte[HeaderSize];
        WriteHeader(header, kind, lsn, payloadLength, recordLength);
        return header;
    }

    private static void WriteHeader(Span<byte> header, byte kind, long lsn, int payloadLength, int recordLength)
    {
        header.Clear(); // Includes the reserved bytes when the destination is pooled.
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        header[4] = FormatVersion;
        header[5] = kind;
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], recordLength);
        BinaryPrimitives.WriteInt64LittleEndian(header[12..], lsn);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], checked((uint)payloadLength));
    }

    private static async ValueTask WriteHashedAsync(Stream stream, ReadOnlyMemory<byte> bytes,
        IncrementalHash hash, CancellationToken cancellationToken)
    {
        hash.AppendData(bytes.Span);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteInt32Async(Stream stream, int value, IncrementalHash hash,
        CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        await WriteHashedAsync(stream, bytes, hash, cancellationToken).ConfigureAwait(false);
    }

    private static int StringLength(string value)
    {
        int bytes = Utf8.GetByteCount(value);
        ValidateStringLength(bytes);
        return checked(sizeof(int) + bytes);
    }

    private static void ValidateStringLength(int length)
    {
        if (length < 0 || length > MaximumStringBytes)
        {
            throw new PhysicalPageCapacityException("A durable transaction string exceeds the supported size.");
        }
    }

    private static void WriteGuid(Span<byte> destination, ref int cursor, Guid value)
    {
        if (!value.TryWriteBytes(destination[cursor..]))
        {
            throw new PhysicalPageCapacityException("A durable transaction identity could not be encoded.");
        }

        cursor += GuidLength;
    }

    private static void WriteInt32(Span<byte> destination, ref int cursor, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination[cursor..], value);
        cursor += sizeof(int);
    }

    private static void WriteInt64(Span<byte> destination, ref int cursor, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination[cursor..], value);
        cursor += sizeof(long);
    }

    private static void WriteString(Span<byte> destination, ref int cursor, string value)
    {
        int length = Utf8.GetByteCount(value);
        ValidateStringLength(length);
        WriteInt32(destination, ref cursor, length);
        cursor += Utf8.GetBytes(value, destination[cursor..]);
    }

    private static void WriteMemory(Span<byte> destination, ref int cursor, ReadOnlyMemory<byte> value)
    {
        WriteInt32(destination, ref cursor, value.Length);
        value.Span.CopyTo(destination[cursor..]);
        cursor += value.Length;
    }

    private static Guid ReadGuid(ReadOnlySpan<byte> payload, ref int cursor)
    {
        EnsureAvailable(payload, cursor, GuidLength, "identity");
        Guid result = new(payload.Slice(cursor, GuidLength));
        cursor += GuidLength;
        return result;
    }

    private static byte ReadByte(ReadOnlySpan<byte> payload, ref int cursor, string component)
    {
        EnsureAvailable(payload, cursor, sizeof(byte), component);
        return payload[cursor++];
    }

    private static long ReadInt64(ReadOnlySpan<byte> payload, ref int cursor, string component)
    {
        EnsureAvailable(payload, cursor, sizeof(long), component);
        long result = BinaryPrimitives.ReadInt64LittleEndian(payload[cursor..]);
        cursor += sizeof(long);
        return result;
    }

    private static string ReadString(ReadOnlySpan<byte> payload, ref int cursor)
    {
        int length = ReadInteger(payload, ref cursor, "string length");
        if (length < 0 || length > MaximumStringBytes)
        {
            throw new RedoWalCorruptionException("A redo transaction string length is invalid.");
        }

        EnsureAvailable(payload, cursor, length, "string");
        string result = Utf8.GetString(payload.Slice(cursor, length));
        cursor += length;
        return result;
    }

    private static byte[] ReadMemory(ReadOnlySpan<byte> payload, ref int cursor, string component)
    {
        int length = ReadInteger(payload, ref cursor, $"{component} length");
        if (length < 0)
        {
            throw new RedoWalCorruptionException($"A redo {component} length is invalid.");
        }

        EnsureAvailable(payload, cursor, length, component);
        byte[] result = payload.Slice(cursor, length).ToArray();
        cursor += length;
        return result;
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> payload, int cursor, int length, string component)
    {
        if (cursor < 0 || length < 0 || cursor > payload.Length - length)
        {
            throw new RedoWalCorruptionException($"A redo transaction {component} is truncated.");
        }
    }

    private static int ReadInteger(ReadOnlySpan<byte> payload, ref int cursor, string component)
    {
        if (cursor + IntegerLength > payload.Length)
        {
            throw new RedoWalCorruptionException($"A redo {component} is truncated.");
        }

        int result = BinaryPrimitives.ReadInt32LittleEndian(payload[cursor..]);
        cursor += IntegerLength;
        return result;
    }
}

internal readonly record struct DecodedRedoRecord(byte Kind, long Lsn, byte[] Payload);

internal readonly record struct RedoRecordMetadata(byte Kind, long Lsn, int PayloadLength);
