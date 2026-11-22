using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace EntityForge.Db.Prototypes.Pages;

public enum DurabilityStrategyKind
{
    RedoWal = 0, NoOverwrite = 1
}

public enum DurabilityFaultPoint
{
    None = 0,
    BeforeDurableCommit = 1,
    AfterDurableCommitBeforePublication = 2,
    AfterPublicationBeforeAcknowledgement = 3
}

public enum CheckpointFaultPoint
{
    None = 0,
    BeforePublication = 1,
    AfterPublicationBeforeReclamation = 2,
    AfterReclamation = 3
}

public sealed record DurabilityMutation(long PageId, byte[] Image);

public sealed record DurabilityTransaction(long TransactionId, IReadOnlyList<DurabilityMutation> Mutations);

public sealed record DurabilityMetrics(long DurableBytes, long FlushCount, long PublishedTransactions,
    long AcknowledgedTransactions, long CheckpointBytes, long CheckpointCount,
    double CheckpointMilliseconds, long RetainedBytes, long RecoveryBytes, double RecoveryMilliseconds);

public interface IDurabilityPrototypeStore : IAsyncDisposable
{
    ValueTask ApplyAsync(DurabilityTransaction transaction, DurabilityFaultPoint faultPoint,
        CancellationToken cancellationToken = default);

    ValueTask ApplyBatchAsync(IReadOnlyList<DurabilityTransaction> transactions,
        DurabilityFaultPoint faultPoint, CancellationToken cancellationToken = default);

    ValueTask CreateCheckpointAsync(CheckpointFaultPoint faultPoint = CheckpointFaultPoint.None,
        CancellationToken cancellationToken = default);

    IReadOnlyDictionary<long, byte[]> CurrentPages
    {
        get;
    }

    DurabilityMetrics Metrics
    {
        get;
    }
}

public static class DurabilityPrototypeStore
{
    public static async ValueTask<IDurabilityPrototypeStore> OpenAsync(DurabilityStrategyKind strategy,
        string directory, int pageSize, CancellationToken cancellationToken = default) => strategy switch
        {
            DurabilityStrategyKind.RedoWal => await RedoWalPrototypeStore.OpenAsync(directory, pageSize,
                cancellationToken).ConfigureAwait(false),
            DurabilityStrategyKind.NoOverwrite => await NoOverwritePrototypeStore.OpenAsync(directory, pageSize,
                cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy))
        };
}

internal abstract class DurabilityPrototypeStoreBase : IDurabilityPrototypeStore
{
    protected const uint TransactionMagic = 0x45465458;
    protected const int HeaderSize = 24;
    protected const int MutationHeaderSize = 16;
    protected const int ChecksumSize = 4;
    private long _published;
    private long _acknowledged;
    private long _checkpointBytes;
    private long _checkpointCount;
    private double _checkpointMilliseconds;
    protected readonly Dictionary<long, byte[]> Pages = [];

    protected DurabilityPrototypeStoreBase(int pageSize) => PageSize = pageSize;

    protected int PageSize
    {
        get;
    }

    protected long DurableBytes
    {
        get; set;
    }

    protected long FlushCount
    {
        get; set;
    }

    protected long RecoveryBytes
    {
        get; set;
    }

    protected double RecoveryMilliseconds
    {
        get; set;
    }

    protected long RetainedBytes
    {
        get; set;
    }

    public IReadOnlyDictionary<long, byte[]> CurrentPages => Pages;

    public DurabilityMetrics Metrics => new(DurableBytes, FlushCount, _published, _acknowledged,
        _checkpointBytes, _checkpointCount, _checkpointMilliseconds, RetainedBytes,
        RecoveryBytes, RecoveryMilliseconds);

    public ValueTask ApplyAsync(DurabilityTransaction transaction, DurabilityFaultPoint faultPoint,
        CancellationToken cancellationToken = default) =>
        ApplyBatchAsync([transaction], faultPoint, cancellationToken);

    public abstract ValueTask ApplyBatchAsync(IReadOnlyList<DurabilityTransaction> transactions,
        DurabilityFaultPoint faultPoint, CancellationToken cancellationToken = default);

    public abstract ValueTask CreateCheckpointAsync(CheckpointFaultPoint faultPoint = CheckpointFaultPoint.None,
        CancellationToken cancellationToken = default);

    public abstract ValueTask DisposeAsync();

    protected byte[] Encode(DurabilityTransaction transaction)
    {
        if (transaction.TransactionId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transaction));
        }

        if (transaction.Mutations.Count == 0)
        {
            throw new ArgumentException("A transaction needs mutations.");
        }

        int length = checked(HeaderSize + ChecksumSize + transaction.Mutations.Sum(_ => MutationHeaderSize + PageSize));
        var record = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, TransactionMagic);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), length);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), transaction.TransactionId);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(16), transaction.Mutations.Count);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20), PageSize);
        int offset = HeaderSize;
        foreach (DurabilityMutation mutation in transaction.Mutations)
        {
            if (mutation.PageId < 0 || mutation.Image.Length != PageSize)
            {
                throw new ArgumentException("A mutation has invalid page identity or image length.");
            }

            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset), mutation.PageId);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset + sizeof(long)), transaction.TransactionId);
            mutation.Image.CopyTo(record, offset + MutationHeaderSize);
            offset += MutationHeaderSize + PageSize;
        }
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(record.AsSpan(0, length - ChecksumSize)));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(length - ChecksumSize), checksum);
        return record;
    }

    protected IEnumerable<DurabilityTransaction> DecodeRecords(byte[] bytes)
    {
        int offset = 0;
        while (offset + HeaderSize + ChecksumSize <= bytes.Length)
        {
            ReadOnlySpan<byte> remaining = bytes.AsSpan(offset);
            if (BinaryPrimitives.ReadUInt32LittleEndian(remaining) != TransactionMagic)
            {
                yield break;
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(remaining[4..]);
            if (length < HeaderSize + ChecksumSize || offset + length > bytes.Length)
            {
                yield break;
            }

            ReadOnlySpan<byte> record = bytes.AsSpan(offset, length);
            uint expected = BinaryPrimitives.ReadUInt32LittleEndian(record[^ChecksumSize..]);
            uint actual = BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(record[..^ChecksumSize]));
            if (expected != actual)
            {
                yield break;
            }

            long transactionId = BinaryPrimitives.ReadInt64LittleEndian(record[8..]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(record[16..]);
            int encodedPageSize = BinaryPrimitives.ReadInt32LittleEndian(record[20..]);
            if (encodedPageSize != PageSize || count <= 0 ||
                length != HeaderSize + ChecksumSize + count * (MutationHeaderSize + PageSize))
            {
                yield break;
            }

            var mutations = new DurabilityMutation[count];
            int mutationOffset = HeaderSize;
            for (int index = 0; index < count; index++)
            {
                long pageId = BinaryPrimitives.ReadInt64LittleEndian(record[mutationOffset..]);
                mutations[index] = new(pageId,
                    record.Slice(mutationOffset + MutationHeaderSize, PageSize).ToArray());
                mutationOffset += MutationHeaderSize + PageSize;
            }
            yield return new(transactionId, mutations);
            offset += length;
        }
    }

    protected void Publish(DurabilityTransaction transaction)
    {
        foreach (DurabilityMutation mutation in transaction.Mutations)
        {
            Pages[mutation.PageId] = mutation.Image;
        }

        _published++;
    }

    protected void Acknowledge() => _acknowledged++;

    protected void RecordCheckpoint(long bytes, double milliseconds)
    {
        _checkpointBytes += bytes;
        _checkpointCount++;
        _checkpointMilliseconds += milliseconds;
    }

    protected DurabilityTransaction SnapshotTransaction() => new(long.MaxValue,
        Pages.OrderBy(item => item.Key).Select(item => new DurabilityMutation(item.Key, item.Value)).ToArray());

    protected static void ThrowIf(DurabilityFaultPoint actual, DurabilityFaultPoint requested)
    {
        if (actual == requested)
        {
            throw new InjectedDurabilityFaultException(requested);
        }
    }

    protected static async Task WriteTemporaryAsync(string temporaryPath, byte[] bytes,
        int bufferSize, CancellationToken cancellationToken)
    {
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         bufferSize, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    protected static void ThrowIf(CheckpointFaultPoint actual, CheckpointFaultPoint requested)
    {
        if (actual == requested)
        {
            throw new InjectedCheckpointFaultException(requested);
        }
    }
}

public sealed class InjectedDurabilityFaultException : PrototypePageException
{
    public InjectedDurabilityFaultException(DurabilityFaultPoint point) : base($"Injected fault at {point}.") =>
        Point = point;

    public DurabilityFaultPoint Point
    {
        get;
    }
}

public sealed class InjectedCheckpointFaultException : PrototypePageException
{
    public InjectedCheckpointFaultException(CheckpointFaultPoint point) : base($"Injected checkpoint fault at {point}.") =>
        Point = point;

    public CheckpointFaultPoint Point
    {
        get;
    }
}

internal sealed class RedoWalPrototypeStore : DurabilityPrototypeStoreBase
{
    private const string WalFileName = "redo.wal";
    private const string CheckpointFileName = "checkpoint.dat";
    private readonly string _directory;
    private readonly FileStream _wal;

    private RedoWalPrototypeStore(string directory, FileStream wal, int pageSize) : base(pageSize)
    {
        _directory = directory;
        _wal = wal;
    }

    public static async ValueTask<RedoWalPrototypeStore> OpenAsync(string directory, int pageSize,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, WalFileName);
        string checkpointPath = Path.Combine(directory, CheckpointFileName);
        byte[] recoveredCheckpoint = File.Exists(checkpointPath)
            ? await File.ReadAllBytesAsync(checkpointPath, cancellationToken).ConfigureAwait(false) : [];
        byte[] recovered = File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : [];
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            bufferSize: pageSize, FileOptions.Asynchronous | FileOptions.WriteThrough);
        stream.Position = stream.Length;
        var store = new RedoWalPrototypeStore(directory, stream, pageSize);
        long started = Stopwatch.GetTimestamp();
        foreach (DurabilityTransaction transaction in store.DecodeRecords(recoveredCheckpoint))
        {
            store.Publish(transaction);
        }

        foreach (DurabilityTransaction transaction in store.DecodeRecords(recovered))
        {
            store.Publish(transaction);
        }

        store.RecoveryBytes = recoveredCheckpoint.Length + recovered.Length;
        store.RecoveryMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        store.RetainedBytes = recoveredCheckpoint.Length + recovered.Length;
        return store;
    }

    public override async ValueTask CreateCheckpointAsync(
        CheckpointFaultPoint faultPoint = CheckpointFaultPoint.None,
        CancellationToken cancellationToken = default)
    {
        if (Pages.Count == 0)
        {
            return;
        }

        long started = Stopwatch.GetTimestamp();
        byte[] checkpoint = Encode(SnapshotTransaction());
        string target = Path.Combine(_directory, CheckpointFileName);
        string temporary = target + ".new";
        await WriteTemporaryAsync(temporary, checkpoint, PageSize, cancellationToken).ConfigureAwait(false);
        ThrowIf(faultPoint, CheckpointFaultPoint.BeforePublication);
        File.Move(temporary, target, overwrite: true);
        ThrowIf(faultPoint, CheckpointFaultPoint.AfterPublicationBeforeReclamation);
        _wal.SetLength(0);
        _wal.Position = 0;
        await _wal.FlushAsync(cancellationToken).ConfigureAwait(false);
        ThrowIf(faultPoint, CheckpointFaultPoint.AfterReclamation);
        FlushCount += 2;
        DurableBytes += checkpoint.Length;
        RetainedBytes = checkpoint.Length;
        RecordCheckpoint(checkpoint.Length, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public override async ValueTask ApplyBatchAsync(IReadOnlyList<DurabilityTransaction> transactions,
        DurabilityFaultPoint faultPoint,
        CancellationToken cancellationToken = default)
    {
        if (transactions.Count == 0)
        {
            throw new ArgumentException("A durability batch cannot be empty.");
        }

        byte[][] records = transactions.Select(Encode).ToArray();
        long initialLength = _wal.Length;
        foreach (byte[] record in records)
        {
            await _wal.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        }

        if (faultPoint == DurabilityFaultPoint.BeforeDurableCommit)
        {
            _wal.SetLength(initialLength);
            _wal.Position = initialLength;
            ThrowIf(faultPoint, DurabilityFaultPoint.BeforeDurableCommit);
        }
        await _wal.FlushAsync(cancellationToken).ConfigureAwait(false);
        DurableBytes += records.Sum(record => (long)record.Length);
        FlushCount++;
        ThrowIf(faultPoint, DurabilityFaultPoint.AfterDurableCommitBeforePublication);
        foreach (DurabilityTransaction transaction in transactions)
        {
            Publish(transaction);
        }

        ThrowIf(faultPoint, DurabilityFaultPoint.AfterPublicationBeforeAcknowledgement);
        foreach (DurabilityTransaction _ in transactions)
        {
            Acknowledge();
        }
    }

    public override async ValueTask DisposeAsync() => await _wal.DisposeAsync().ConfigureAwait(false);
}

internal sealed class NoOverwritePrototypeStore : DurabilityPrototypeStoreBase
{
    private const uint RootMagic = 0x45465254;
    private const int RootHeaderSize = 20;
    private const int RootEntrySize = 16;
    private const int RootChecksumSize = 4;
    private const string VersionFileName = "versions.dat";
    private const string RootFileName = "roots.log";
    private const string CheckpointFileName = "checkpoint.dat";
    private readonly string _directory;
    private readonly FileStream _versions;
    private readonly FileStream _roots;

    private NoOverwritePrototypeStore(string directory, FileStream versions, FileStream roots, int pageSize) : base(pageSize)
    {
        _directory = directory;
        _versions = versions;
        _roots = roots;
    }

    public static async ValueTask<NoOverwritePrototypeStore> OpenAsync(string directory, int pageSize,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        string versionsPath = Path.Combine(directory, VersionFileName);
        string rootsPath = Path.Combine(directory, RootFileName);
        string checkpointPath = Path.Combine(directory, CheckpointFileName);
        byte[] recoveredCheckpoint = File.Exists(checkpointPath)
            ? await File.ReadAllBytesAsync(checkpointPath, cancellationToken).ConfigureAwait(false) : [];
        byte[] recoveredRoots = File.Exists(rootsPath)
            ? await File.ReadAllBytesAsync(rootsPath, cancellationToken).ConfigureAwait(false) : [];
        byte[] recoveredVersions = File.Exists(versionsPath)
            ? await File.ReadAllBytesAsync(versionsPath, cancellationToken).ConfigureAwait(false) : [];
        var versions = new FileStream(versionsPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            pageSize, FileOptions.Asynchronous | FileOptions.WriteThrough);
        var roots = new FileStream(rootsPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            pageSize, FileOptions.Asynchronous | FileOptions.WriteThrough);
        versions.Position = versions.Length;
        roots.Position = roots.Length;
        var store = new NoOverwritePrototypeStore(directory, versions, roots, pageSize);
        long started = Stopwatch.GetTimestamp();
        foreach (DurabilityTransaction transaction in store.DecodeRecords(recoveredCheckpoint))
        {
            store.Publish(transaction);
        }

        foreach (DurabilityTransaction transaction in store.DecodeRootRecords(recoveredRoots, recoveredVersions))
        {
            store.Publish(transaction);
        }

        store.RecoveryBytes = recoveredCheckpoint.Length + recoveredRoots.Length + recoveredVersions.Length;
        store.RecoveryMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        store.RetainedBytes = store.RecoveryBytes;
        return store;
    }

    public override async ValueTask CreateCheckpointAsync(
        CheckpointFaultPoint faultPoint = CheckpointFaultPoint.None,
        CancellationToken cancellationToken = default)
    {
        if (Pages.Count == 0)
        {
            return;
        }

        long started = Stopwatch.GetTimestamp();
        byte[] checkpoint = Encode(SnapshotTransaction());
        string target = Path.Combine(_directory, CheckpointFileName);
        string temporary = target + ".new";
        await WriteTemporaryAsync(temporary, checkpoint, PageSize, cancellationToken).ConfigureAwait(false);
        ThrowIf(faultPoint, CheckpointFaultPoint.BeforePublication);
        File.Move(temporary, target, overwrite: true);
        ThrowIf(faultPoint, CheckpointFaultPoint.AfterPublicationBeforeReclamation);
        _versions.SetLength(0);
        _versions.Position = 0;
        _roots.SetLength(0);
        _roots.Position = 0;
        await _versions.FlushAsync(cancellationToken).ConfigureAwait(false);
        await _roots.FlushAsync(cancellationToken).ConfigureAwait(false);
        ThrowIf(faultPoint, CheckpointFaultPoint.AfterReclamation);
        FlushCount += 3;
        DurableBytes += checkpoint.Length;
        RetainedBytes = checkpoint.Length;
        RecordCheckpoint(checkpoint.Length, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public override async ValueTask ApplyBatchAsync(IReadOnlyList<DurabilityTransaction> transactions,
        DurabilityFaultPoint faultPoint,
        CancellationToken cancellationToken = default)
    {
        if (transactions.Count == 0)
        {
            throw new ArgumentException("A durability batch cannot be empty.");
        }

        var offsetsByTransaction = new List<long[]>(transactions.Count);
        long initialVersionLength = _versions.Length;
        foreach (DurabilityTransaction transaction in transactions)
        {
            var offsets = new long[transaction.Mutations.Count];
            for (int index = 0; index < transaction.Mutations.Count; index++)
            {
                DurabilityMutation mutation = transaction.Mutations[index];
                offsets[index] = _versions.Position;
                await _versions.WriteAsync(mutation.Image, cancellationToken).ConfigureAwait(false);
            }
            offsetsByTransaction.Add(offsets);
        }
        if (faultPoint == DurabilityFaultPoint.BeforeDurableCommit)
        {
            _versions.SetLength(initialVersionLength);
            _versions.Position = initialVersionLength;
            ThrowIf(faultPoint, DurabilityFaultPoint.BeforeDurableCommit);
        }
        await _versions.FlushAsync(cancellationToken).ConfigureAwait(false);
        byte[][] rootRecords = transactions.Select((transaction, index) =>
            EncodeRoot(transaction, offsetsByTransaction[index])).ToArray();
        foreach (byte[] rootRecord in rootRecords)
        {
            await _roots.WriteAsync(rootRecord, cancellationToken).ConfigureAwait(false);
        }

        await _roots.FlushAsync(cancellationToken).ConfigureAwait(false);
        DurableBytes += transactions.Sum(transaction => (long)transaction.Mutations.Count * PageSize) +
            rootRecords.Sum(record => (long)record.Length);
        FlushCount += 2;
        ThrowIf(faultPoint, DurabilityFaultPoint.AfterDurableCommitBeforePublication);
        foreach (DurabilityTransaction transaction in transactions)
        {
            Publish(transaction);
        }

        ThrowIf(faultPoint, DurabilityFaultPoint.AfterPublicationBeforeAcknowledgement);
        foreach (DurabilityTransaction _ in transactions)
        {
            Acknowledge();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await _versions.DisposeAsync().ConfigureAwait(false);
        await _roots.DisposeAsync().ConfigureAwait(false);
    }

    private static byte[] EncodeRoot(DurabilityTransaction transaction, IReadOnlyList<long> offsets)
    {
        int length = RootHeaderSize + (transaction.Mutations.Count * RootEntrySize) + RootChecksumSize;
        var record = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, RootMagic);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), length);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), transaction.TransactionId);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(16), transaction.Mutations.Count);
        int offset = RootHeaderSize;
        for (int index = 0; index < transaction.Mutations.Count; index++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset), transaction.Mutations[index].PageId);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset + sizeof(long)), offsets[index]);
            offset += RootEntrySize;
        }
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(
            SHA256.HashData(record.AsSpan(0, length - RootChecksumSize)));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(length - RootChecksumSize), checksum);
        return record;
    }

    private IEnumerable<DurabilityTransaction> DecodeRootRecords(byte[] roots, byte[] versions)
    {
        var transactions = new List<DurabilityTransaction>();
        int offset = 0;
        while (offset + RootHeaderSize + RootChecksumSize <= roots.Length)
        {
            ReadOnlySpan<byte> remaining = roots.AsSpan(offset);
            if (BinaryPrimitives.ReadUInt32LittleEndian(remaining) != RootMagic)
            {
                break;
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(remaining[4..]);
            if (length < RootHeaderSize + RootChecksumSize || offset + length > roots.Length)
            {
                break;
            }

            ReadOnlySpan<byte> record = roots.AsSpan(offset, length);
            uint expected = BinaryPrimitives.ReadUInt32LittleEndian(record[^RootChecksumSize..]);
            uint actual = BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(record[..^RootChecksumSize]));
            if (expected != actual)
            {
                break;
            }

            long transactionId = BinaryPrimitives.ReadInt64LittleEndian(record[8..]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(record[16..]);
            if (count <= 0 || length != RootHeaderSize + count * RootEntrySize + RootChecksumSize)
            {
                break;
            }

            var mutations = new DurabilityMutation[count];
            int entryOffset = RootHeaderSize;
            bool valid = true;
            for (int index = 0; index < count; index++)
            {
                long pageId = BinaryPrimitives.ReadInt64LittleEndian(record[entryOffset..]);
                long versionOffset = BinaryPrimitives.ReadInt64LittleEndian(record[(entryOffset + sizeof(long))..]);
                if (versionOffset < 0 || versionOffset + PageSize > versions.Length)
                {
                    valid = false;
                    break;
                }
                mutations[index] = new(pageId, versions.AsSpan(checked((int)versionOffset), PageSize).ToArray());
                entryOffset += RootEntrySize;
            }
            if (!valid)
            {
                break;
            }

            transactions.Add(new(transactionId, mutations));
            offset += length;
        }
        return transactions;
    }
}

public sealed record DurabilityExperimentOptions(IReadOnlyList<int> PageSizes, int TransactionCount,
    int MutationsPerTransaction, int LogicalPageCount, int GroupSize, int Seed)
{
    public static DurabilityExperimentOptions Default
    {
        get;
    }
    = new(
        [8 * 1024, 16 * 1024, 32 * 1024], TransactionCount: 250, MutationsPerTransaction: 4,
        LogicalPageCount: 256, GroupSize: 16, Seed: 0x0E17F04);
}

public sealed record DurabilityExperimentCaseResult(DurabilityStrategyKind Strategy, int PageSize,
    int GroupSize, long DurableBytes, long FlushCount, double WriteMilliseconds, long RecoveryBytes,
    double RecoveryMilliseconds, long CheckpointBytes, double CheckpointMilliseconds,
    long RetainedBytes, string LogicalDigest);

public sealed record DurabilityExperimentResult(int SchemaVersion,
    IReadOnlyList<DurabilityExperimentCaseResult> Results)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public sealed class DurabilityExperiment
{
    public const int ResultSchemaVersion = 1;

    public async Task<DurabilityExperimentResult> RunAsync(DurabilityExperimentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= DurabilityExperimentOptions.Default;
        var results = new List<DurabilityExperimentCaseResult>();
        foreach (int pageSize in options.PageSizes)
        {
            foreach (DurabilityStrategyKind strategy in Enum.GetValues<DurabilityStrategyKind>())
            {
                results.Add(await RunCaseAsync(options, strategy, pageSize, cancellationToken).ConfigureAwait(false));
            }
        }

        return new(ResultSchemaVersion, results);
    }

    private static async Task<DurabilityExperimentCaseResult> RunCaseAsync(DurabilityExperimentOptions options,
        DurabilityStrategyKind strategy, int pageSize, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"entityforge-durability-{Guid.NewGuid():N}");
        try
        {
            DurabilityTransaction[] transactions = Transactions(options, pageSize);
            DurabilityMetrics written;
            long started = Stopwatch.GetTimestamp();
            await using (IDurabilityPrototypeStore store = await DurabilityPrototypeStore.OpenAsync(strategy,
                directory, pageSize, cancellationToken).ConfigureAwait(false))
            {
                int checkpointTransactionCount = options.TransactionCount * 4 / 5;
                foreach (DurabilityTransaction[] group in transactions.Take(checkpointTransactionCount)
                             .Chunk(options.GroupSize))
                {
                    await store.ApplyBatchAsync(group, DurabilityFaultPoint.None, cancellationToken)
                        .ConfigureAwait(false);
                }

                await store.CreateCheckpointAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (DurabilityTransaction[] group in transactions.Skip(checkpointTransactionCount)
                             .Chunk(options.GroupSize))
                {
                    await store.ApplyBatchAsync(group, DurabilityFaultPoint.None, cancellationToken)
                        .ConfigureAwait(false);
                }

                written = store.Metrics;
            }
            double writeMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            await using IDurabilityPrototypeStore recovered = await DurabilityPrototypeStore.OpenAsync(strategy,
                directory, pageSize, cancellationToken).ConfigureAwait(false);
            return new(strategy, pageSize, options.GroupSize, written.DurableBytes, written.FlushCount, writeMilliseconds,
                recovered.Metrics.RecoveryBytes, recovered.Metrics.RecoveryMilliseconds,
                written.CheckpointBytes, written.CheckpointMilliseconds, recovered.Metrics.RetainedBytes,
                Digest(recovered.CurrentPages));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }


    public static string Digest(IReadOnlyDictionary<long, byte[]> pages)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> id = stackalloc byte[sizeof(long)];
        foreach ((long pageId, byte[] image) in pages.OrderBy(item => item.Key))
        {
            BinaryPrimitives.WriteInt64LittleEndian(id, pageId);
            hash.AppendData(id);
            hash.AppendData(image);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static DurabilityTransaction[] Transactions(DurabilityExperimentOptions options, int pageSize)
    {
        var random = new Random(options.Seed);
        return Enumerable.Range(0, options.TransactionCount).Select(transactionIndex =>
            new DurabilityTransaction(transactionIndex, Enumerable.Range(0, options.MutationsPerTransaction)
                .Select(mutationIndex =>
                {
                    long pageId = random.Next(options.LogicalPageCount);
                    var image = new byte[pageSize];
                    random.NextBytes(image);
                    return new DurabilityMutation(pageId, image);
                }).ToArray())).ToArray();
    }
}
