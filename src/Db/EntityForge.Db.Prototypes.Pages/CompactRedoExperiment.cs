using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace EntityForge.Db.Prototypes.Pages;

public sealed record CompactRedoExperimentOptions(IReadOnlyList<int> PageSizes, int TransactionCount,
    int MutationsPerTransaction, int LogicalPageCount, int PatchSize, int GroupSize,
    int CheckpointTransactionCount, int Seed)
{
    public static CompactRedoExperimentOptions Default
    {
        get;
    }
    = new(
        [8 * 1024, 16 * 1024, 32 * 1024], 250, 4, 256, 128, 16, 200, 0x0E17F04);
}

public sealed record CompactRedoPageSizeResult(int PageSize, int FullPageRecords, int PatchRecords,
    long WalBytesWritten, long FullPageBaselineWalBytes, double WalByteRatio,
    long CheckpointBytes, long RetainedBytes, long FullPageBaselineRetainedBytes,
    double RetainedByteRatio, long FlushCount, double RecoveryMilliseconds, string LogicalDigest);

public sealed record CompactRedoExperimentResult(int SchemaVersion,
    IReadOnlyList<CompactRedoPageSizeResult> Results)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public sealed class CompactRedoExperiment
{
    public const int ResultSchemaVersion = 1;
    private const int RecordHeaderSize = 24;
    private const int EntryHeaderSize = 20;
    private const int ChecksumSize = 4;
    private const uint RecordMagic = 0x45464352;
    private const byte FullPageEntry = 1;
    private const byte PatchEntry = 2;

    public async Task<CompactRedoExperimentResult> RunAsync(CompactRedoExperimentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= CompactRedoExperimentOptions.Default;
        Validate(options);
        var results = new List<CompactRedoPageSizeResult>();
        foreach (int pageSize in options.PageSizes)
        {
            results.Add(await RunPageSizeAsync(options, pageSize, cancellationToken).ConfigureAwait(false));
        }

        return new(ResultSchemaVersion, results);
    }

    private static async Task<CompactRedoPageSizeResult> RunPageSizeAsync(CompactRedoExperimentOptions options,
        int pageSize, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"entityforge-compact-redo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string walPath = Path.Combine(directory, "compact.wal");
            string checkpointPath = Path.Combine(directory, "checkpoint.dat");
            var pages = Enumerable.Range(0, options.LogicalPageCount)
                .ToDictionary(index => (long)index, _ => new byte[pageSize]);
            RedoPatchTransaction[] transactions = Transactions(options, pageSize);
            var protectedPages = new HashSet<long>();
            int fullRecords = 0;
            int patchRecords = 0;
            long walBytesWritten = 0;
            long flushCount = 0;

            await using var wal = new FileStream(walPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read,
                pageSize, FileOptions.Asynchronous | FileOptions.WriteThrough);
            foreach (RedoPatchTransaction[] group in transactions.Take(options.CheckpointTransactionCount)
                         .Chunk(options.GroupSize))
            {
                foreach (RedoPatchTransaction transaction in group)
                {
                    byte[] record = EncodeTransaction(transaction, pages, protectedPages, pageSize,
                        ref fullRecords, ref patchRecords);
                    walBytesWritten += record.Length;
                    await wal.WriteAsync(record, cancellationToken).ConfigureAwait(false);
                }
                await wal.FlushAsync(cancellationToken).ConfigureAwait(false);
                flushCount++;
            }

            byte[] checkpoint = EncodeCheckpoint(pages, pageSize);
            await File.WriteAllBytesAsync(checkpointPath, checkpoint, cancellationToken).ConfigureAwait(false);
            wal.SetLength(0);
            wal.Position = 0;
            await wal.FlushAsync(cancellationToken).ConfigureAwait(false);
            flushCount += 2;
            protectedPages.Clear();

            foreach (RedoPatchTransaction[] group in transactions.Skip(options.CheckpointTransactionCount)
                         .Chunk(options.GroupSize))
            {
                foreach (RedoPatchTransaction transaction in group)
                {
                    byte[] record = EncodeTransaction(transaction, pages, protectedPages, pageSize,
                        ref fullRecords, ref patchRecords);
                    walBytesWritten += record.Length;
                    await wal.WriteAsync(record, cancellationToken).ConfigureAwait(false);
                }
                await wal.FlushAsync(cancellationToken).ConfigureAwait(false);
                flushCount++;
            }
            await wal.DisposeAsync().ConfigureAwait(false);

            byte[] retainedWal = await File.ReadAllBytesAsync(walPath, cancellationToken).ConfigureAwait(false);
            byte[] retainedCheckpoint = await File.ReadAllBytesAsync(checkpointPath, cancellationToken)
                .ConfigureAwait(false);
            long recoveryStarted = Stopwatch.GetTimestamp();
            Dictionary<long, byte[]> recovered = DecodeCheckpoint(retainedCheckpoint, pageSize);
            ApplyRecords(retainedWal, pageSize, recovered);
            double recoveryMilliseconds = Stopwatch.GetElapsedTime(recoveryStarted).TotalMilliseconds;
            string expectedDigest = DurabilityExperiment.Digest(pages);
            string recoveredDigest = DurabilityExperiment.Digest(recovered);
            if (!string.Equals(expectedDigest, recoveredDigest, StringComparison.Ordinal))
            {
                throw new PrototypePageCorruptionException("Compact redo recovery produced a different digest.");
            }

            long retainedBytes = retainedCheckpoint.Length + retainedWal.Length;
            long fullPageBaseline = retainedCheckpoint.Length +
                (long)(options.TransactionCount - options.CheckpointTransactionCount) *
                options.MutationsPerTransaction * pageSize;
            long fullPageBaselineWal = (long)options.TransactionCount * options.MutationsPerTransaction * pageSize;
            return new(pageSize, fullRecords, patchRecords, walBytesWritten, fullPageBaselineWal,
                (double)walBytesWritten / fullPageBaselineWal, checkpoint.Length, retainedBytes,
                fullPageBaseline, (double)retainedBytes / fullPageBaseline, flushCount,
                recoveryMilliseconds, recoveredDigest);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static byte[] EncodeTransaction(RedoPatchTransaction transaction,
        IDictionary<long, byte[]> pages, ISet<long> protectedPages, int pageSize,
        ref int fullRecordCount, ref int patchRecordCount)
    {
        var entries = new List<EncodedRedoEntry>(transaction.Patches.Count);
        foreach (RedoPatch patch in transaction.Patches)
        {
            byte[] page = pages[patch.PageId];
            patch.Bytes.CopyTo(page, patch.Offset);
            if (protectedPages.Add(patch.PageId))
            {
                entries.Add(new(FullPageEntry, patch.PageId, 0, page.ToArray()));
                fullRecordCount++;
            }
            else
            {
                entries.Add(new(PatchEntry, patch.PageId, patch.Offset, patch.Bytes));
                patchRecordCount++;
            }
        }
        return EncodeRecord(transaction.TransactionId, entries);
    }

    private static byte[] EncodeCheckpoint(IReadOnlyDictionary<long, byte[]> pages, int pageSize) =>
        EncodeRecord(long.MaxValue, pages.OrderBy(item => item.Key)
            .Select(item => new EncodedRedoEntry(FullPageEntry, item.Key, 0, item.Value)).ToArray());

    private static byte[] EncodeRecord(long transactionId, IReadOnlyList<EncodedRedoEntry> entries)
    {
        int length = checked(RecordHeaderSize + ChecksumSize +
            entries.Sum(entry => EntryHeaderSize + entry.Bytes.Length));
        var record = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, RecordMagic);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), length);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), transactionId);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(16), entries.Count);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20), 0);
        int offset = RecordHeaderSize;
        foreach (EncodedRedoEntry entry in entries)
        {
            record[offset] = entry.Kind;
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset + 4), entry.PageId);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(offset + 12), entry.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(offset + 16), entry.Bytes.Length);
            entry.Bytes.CopyTo(record, offset + EntryHeaderSize);
            offset += EntryHeaderSize + entry.Bytes.Length;
        }
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(record.AsSpan(0, length - ChecksumSize)));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(length - ChecksumSize), checksum);
        return record;
    }

    private static Dictionary<long, byte[]> DecodeCheckpoint(byte[] checkpoint, int pageSize)
    {
        var pages = new Dictionary<long, byte[]>();
        ApplyRecords(checkpoint, pageSize, pages);
        return pages;
    }

    private static void ApplyRecords(byte[] records, int pageSize, IDictionary<long, byte[]> pages)
    {
        int offset = 0;
        while (offset < records.Length)
        {
            if (offset + RecordHeaderSize + ChecksumSize > records.Length)
            {
                throw new PrototypePageCorruptionException("A compact redo record is truncated.");
            }

            ReadOnlySpan<byte> recordStart = records.AsSpan(offset);
            if (BinaryPrimitives.ReadUInt32LittleEndian(recordStart) != RecordMagic)
            {
                throw new PrototypePageCorruptionException("A compact redo record has invalid magic.");
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(recordStart[4..]);
            if (length < RecordHeaderSize + ChecksumSize || offset + length > records.Length)
            {
                throw new PrototypePageCorruptionException("A compact redo record has invalid length.");
            }

            ReadOnlySpan<byte> record = records.AsSpan(offset, length);
            uint expected = BinaryPrimitives.ReadUInt32LittleEndian(record[^ChecksumSize..]);
            uint actual = BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(record[..^ChecksumSize]));
            if (expected != actual)
            {
                throw new PrototypePageCorruptionException("A compact redo checksum is invalid.");
            }

            int count = BinaryPrimitives.ReadInt32LittleEndian(record[16..]);
            int entryOffset = RecordHeaderSize;
            for (int index = 0; index < count; index++)
            {
                if (entryOffset + EntryHeaderSize > length - ChecksumSize)
                {
                    throw new PrototypePageCorruptionException("A compact redo entry is truncated.");
                }

                byte kind = record[entryOffset];
                long pageId = BinaryPrimitives.ReadInt64LittleEndian(record[(entryOffset + 4)..]);
                int patchOffset = BinaryPrimitives.ReadInt32LittleEndian(record[(entryOffset + 12)..]);
                int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(record[(entryOffset + 16)..]);
                entryOffset += EntryHeaderSize;
                if (payloadLength <= 0 || entryOffset + payloadLength > length - ChecksumSize)
                {
                    throw new PrototypePageCorruptionException("A compact redo payload is invalid.");
                }

                ReadOnlySpan<byte> payload = record.Slice(entryOffset, payloadLength);
                if (kind == FullPageEntry)
                {
                    if (patchOffset != 0 || payloadLength != pageSize)
                    {
                        throw new PrototypePageCorruptionException("A protected page image is invalid.");
                    }

                    pages[pageId] = payload.ToArray();
                }
                else if (kind == PatchEntry)
                {
                    if (!pages.TryGetValue(pageId, out byte[]? page) || patchOffset < 0 ||
                        patchOffset + payloadLength > pageSize)
                    {
                        throw new PrototypePageCorruptionException("A compact redo patch has no valid base page.");
                    }

                    payload.CopyTo(page.AsSpan(patchOffset));
                }
                else
                {
                    throw new PrototypePageCorruptionException("A compact redo entry kind is unsupported.");
                }

                entryOffset += payloadLength;
            }
            if (entryOffset != length - ChecksumSize)
            {
                throw new PrototypePageCorruptionException("A compact redo record has trailing payload.");
            }

            offset += length;
        }
    }

    private static RedoPatchTransaction[] Transactions(CompactRedoExperimentOptions options, int pageSize)
    {
        var random = new Random(options.Seed);
        return Enumerable.Range(0, options.TransactionCount).Select(transactionId =>
            new RedoPatchTransaction(transactionId, Enumerable.Range(0, options.MutationsPerTransaction)
                .Select(_ =>
                {
                    long pageId = random.Next(options.LogicalPageCount);
                    int offset = random.Next(pageSize - options.PatchSize + 1);
                    var bytes = new byte[options.PatchSize];
                    random.NextBytes(bytes);
                    return new RedoPatch(pageId, offset, bytes);
                }).ToArray())).ToArray();
    }

    private static void Validate(CompactRedoExperimentOptions options)
    {
        if (options.PageSizes.Count == 0 || options.PageSizes.Any(size => size <= options.PatchSize))
        {
            throw new ArgumentOutOfRangeException(nameof(options.PageSizes));
        }

        if (options.TransactionCount <= 0 || options.MutationsPerTransaction <= 0 ||
            options.LogicalPageCount <= 0 || options.PatchSize <= 0 || options.GroupSize <= 0 ||
            options.CheckpointTransactionCount <= 0 || options.CheckpointTransactionCount >= options.TransactionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private sealed record RedoPatchTransaction(long TransactionId, IReadOnlyList<RedoPatch> Patches);

    private sealed record RedoPatch(long PageId, int Offset, byte[] Bytes);

    private sealed record EncodedRedoEntry(byte Kind, long PageId, int Offset, byte[] Bytes);
}
