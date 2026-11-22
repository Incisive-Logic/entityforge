using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace EntityForge.Db.Query.Planning;

// The caller supplies language-specific comparison and lossless serialization.
// Temporary runs never become database files or durable query continuations.
internal sealed class ExternalMergeSort<T>(IComparer<T> comparer, Action<T, Stream> encode, Func<byte[], T> decode,
    int maximumBufferedRows = 4096, int maximumBufferedBytes = 8 * 1024 * 1024,
    int maximumRecordBytes = 1024 * 1024, long maximumDiskBytes = 256L * 1024 * 1024,
    ExternalSortDiskBudget? sharedDiskBudget = null) : IExternalSort<T>
{
    private const int FanIn = 16;
    private const int MaximumRuns = 256;
    private readonly List<(T Value, byte[] Bytes)> _buffer = [];
    private readonly List<string> _runs = [];
    private readonly Dictionary<string, long> _files = [];
    private readonly ExternalSortDiskBudget _budget = sharedDiskBudget ?? new(maximumDiskBytes);
    private long _diskBytes;
    private int _bufferBytes;
    private bool _reading;
    private bool _disposed;
    private bool _faulted;
    private QuerySortScratch? _scratch;

    internal string? ScratchDirectory
    {
        get; private set;
    }

    public async ValueTask AddAsync(T value, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reading || _faulted)
        {
            throw new InvalidOperationException("Cannot append after sorting has begun or an append has failed.");
        }
        Validate();
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var encoded = new BoundedSortRecordStream(Math.Min(maximumRecordBytes, maximumBufferedBytes));
            encode(value, encoded);
            byte[] bytes = encoded.ToArray();
            if (bytes.Length == 0)
            {
                throw new InvalidDataException("Sort codec returned an empty record.");
            }
            if (bytes.Length > maximumRecordBytes || bytes.Length > maximumBufferedBytes)
            {
                throw new QuerySortBudgetException("Sort record exceeds its encoded-size budget.");
            }
            if (_buffer.Count >= maximumBufferedRows || bytes.Length > maximumBufferedBytes - _bufferBytes)
            {
                await FlushAsync(cancellation).ConfigureAwait(false);
            }
            // Compare the encoded snapshot, not a caller-owned mutable object
            // that could change before this buffer is flushed.
            _buffer.Add((decode(bytes), bytes));
            _bufferBytes += bytes.Length;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    public async IAsyncEnumerable<T> ReadAsync([EnumeratorCancellation] CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reading || _faulted)
        {
            throw new InvalidOperationException("A sort can be consumed only once.");
        }
        Validate();
        _reading = true;
        await FlushAsync(cancellation).ConfigureAwait(false);
        while (_runs.Count > FanIn)
        {
            var next = new List<string>();
            foreach (string[] group in _runs.Chunk(FanIn))
            {
                string output = CreateRun();
                await using (var stream = OpenWrite(output))
                {
                    await foreach (var row in MergeAsync(group, cancellation).ConfigureAwait(false))
                    {
                        await WriteAsync(output, stream, row.Bytes, cancellation).ConfigureAwait(false);
                    }
                }
                foreach (string input in group)
                {
                    DeleteRun(input);
                }
                next.Add(output);
            }
            _runs.Clear();
            _runs.AddRange(next);
        }
        await foreach (var row in MergeAsync(_runs, cancellation).ConfigureAwait(false))
        {
            yield return row.Value;
        }
    }

    private void Validate()
    {
        ArgumentNullException.ThrowIfNull(comparer);
        ArgumentNullException.ThrowIfNull(encode);
        ArgumentNullException.ThrowIfNull(decode);
        if (maximumBufferedRows < 1 || maximumBufferedBytes < 1 || maximumRecordBytes < 1 || maximumDiskBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBufferedRows), "Sort budgets must be positive.");
        }
    }

    private async ValueTask FlushAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_buffer.Count == 0)
        {
            return;
        }
        if (_runs.Count == MaximumRuns)
        {
            throw new QuerySortBudgetException("Sort exceeds its temporary-run count budget.");
        }
        try
        {
            QueryComparisonSort.Sort(_buffer, (left, right) =>
            {
                cancellation.ThrowIfCancellationRequested();
                return comparer.Compare(left.Value, right.Value);
            });
        }
        catch (InvalidOperationException) when (cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellation);
        }
        string path = CreateRun();
        await using (var stream = OpenWrite(path))
        {
            foreach (var row in _buffer)
            {
                await WriteAsync(path, stream, row.Bytes, cancellation).ConfigureAwait(false);
            }
        }
        _runs.Add(path);
        _buffer.Clear();
        _bufferBytes = 0;
    }

    private string CreateRun()
    {
        _scratch ??= QuerySortScratch.Create();
        ScratchDirectory = _scratch.DirectoryPath;
        string path = Path.Combine(ScratchDirectory, Guid.NewGuid().ToString("N") + ".run");
        _files.Add(path, 0);
        return path;
    }

    private static FileStream OpenWrite(string path) => new(path, FileMode.CreateNew, FileAccess.Write,
        FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private async ValueTask WriteAsync(string path, FileStream stream, byte[] bytes, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        long size = sizeof(int) + (long)bytes.Length + SHA256.HashSizeInBytes;
        if (size > maximumDiskBytes - _diskBytes)
        {
            throw new QuerySortBudgetException("Sort exceeds its temporary-disk byte budget.");
        }
        // Reserve before writing, including failed/partial writes, so no retry
        // can underestimate occupied disk. Failed operations are disposed.
        _budget.Reserve(size);
        _diskBytes += size;
        _files[path] += size;
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellation).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellation).ConfigureAwait(false);
        await stream.WriteAsync(SHA256.HashData(bytes), cancellation).ConfigureAwait(false);
    }

    private async ValueTask<(T Value, byte[] Bytes)?> ReadRecordAsync(FileStream stream, CancellationToken cancellation)
    {
        byte[] header = new byte[sizeof(int)];
        int read = await stream.ReadAsync(header, cancellation).ConfigureAwait(false);
        if (read == 0)
        {
            if (stream.Position != _files[stream.Name])
            {
                throw new InvalidDataException("Temporary sort run ended before its recorded length.");
            }
            return null;
        }
        await stream.ReadExactlyAsync(header.AsMemory(read), cancellation).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size < 1 || size > maximumRecordBytes)
        {
            throw new InvalidDataException("Invalid temporary sort record length.");
        }
        byte[] bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
        byte[] checksum = new byte[SHA256.HashSizeInBytes];
        await stream.ReadExactlyAsync(checksum, cancellation).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(checksum, SHA256.HashData(bytes)))
        {
            throw new InvalidDataException("Temporary sort record checksum mismatch.");
        }
        return (decode(bytes), bytes);
    }

    private async IAsyncEnumerable<(T Value, byte[] Bytes)> MergeAsync(IEnumerable<string> paths,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var streams = new List<FileStream>();
        var queue = new PriorityQueue<(int Index, T Value, byte[] Bytes), T>(comparer);
        try
        {
            foreach (string path in paths)
            {
                cancellation.ThrowIfCancellationRequested();
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                streams.Add(stream);
                if (stream.Length != _files[path])
                {
                    throw new InvalidDataException("Temporary sort run length changed.");
                }
                if (await ReadRecordAsync(stream, cancellation).ConfigureAwait(false) is { } row)
                {
                    queue.Enqueue((streams.Count - 1, row.Value, row.Bytes), row.Value);
                }
            }
            while (queue.TryDequeue(out var current, out _))
            {
                cancellation.ThrowIfCancellationRequested();
                yield return (current.Value, current.Bytes);
                if (await ReadRecordAsync(streams[current.Index], cancellation).ConfigureAwait(false) is { } next)
                {
                    queue.Enqueue((current.Index, next.Value, next.Bytes), next.Value);
                }
            }
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void DeleteRun(string path)
    {
        File.Delete(path);
        _budget.Release(_files[path]);
        _diskBytes -= _files[path];
        _files.Remove(path);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        try
        {
            foreach (string path in _files.Keys.ToArray())
            {
                DeleteRun(path);
            }
        }
        finally
        {
            _scratch?.Dispose();
        }
        _buffer.Clear();
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
