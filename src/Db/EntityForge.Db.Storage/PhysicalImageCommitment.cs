using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EntityForge.Db.Storage;

/// <summary>
/// Computes a length-bound, domain-separated SHA-256 commitment over fixed-size image blocks.
/// A writer-owned instance reuses only blocks untouched by any attempted physical mutation.
/// Independent verification uses a new instance and reads the complete image.
/// </summary>
public sealed class PhysicalImageCommitment
{
    /// <summary>Size of each commitment block in bytes: one MiB.</summary>
    public const int BlockBytes = 1024 * 1024;
    private const int MaximumCachedBlocks = 1024 * 1024;
    private static readonly byte[] Domain = "EntityForge.PhysicalImage.BlockSha256.v1"u8.ToArray();
    private static readonly Meter Meter = new("EntityForge.Db.Storage.ImageCommitment");
    private static readonly Histogram<double> PhaseDuration = Meter.CreateHistogram<double>(
        "entityforge.db.storage.image_commitment.phase.duration", "ms");

    private static readonly Histogram<long> ImageBytes = Meter.CreateHistogram<long>(
        "entityforge.db.storage.image_commitment.bytes", "By");

    private static readonly Histogram<long> ReadBytes = Meter.CreateHistogram<long>(
        "entityforge.db.storage.image_commitment.read.bytes", "By");

    private static void RecordPhase(string phase, long ticks) => PhaseDuration.Record(
        ticks * 1000.0 / Stopwatch.Frequency, new KeyValuePair<string, object?>("entityforge.db.phase", phase));

    private readonly string _path;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly Dictionary<long, BlockDigest> _blocks = [];
    private long _generation;
    private int _writers;
    private bool _closed;

    /// <summary>Creates a writer-owned commitment cache for a physical image file.</summary>
    /// <param name="path">Image path, resolved to an absolute path when this instance is created.</param>
    /// <remarks>Every attempted write or resize must be tracked through this instance for cached hashes to remain valid.
    /// Use a new instance for independent verification rather than trusting a writer's cache.</remarks>
    public PhysicalImageCommitment(string path) => _path = Path.GetFullPath(path);

    internal string ImagePath => _path;

    /// <summary>Invalidate before attempting I/O, including I/O that fails ambiguously.</summary>
    public IDisposable BeginWrite(long offset, int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        long last = checked(offset + bytes - 1) / BlockBytes;
        lock (_gate)
        {
            for (long block = offset / BlockBytes; block <= last; block++)
            {
                _blocks.Remove(block);
            }

            return BeginMutation();
        }
    }

    /// <summary>Invalidate the old partial block on growth and all removed blocks on truncation.</summary>
    public IDisposable BeginResize(long previousBytes, long nextBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(previousBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(nextBytes);
        lock (_gate)
        {
            if (nextBytes < previousBytes)
            {
                foreach (long block in _blocks.Keys.Where(key => key >= nextBytes / BlockBytes).ToArray())
                {
                    _blocks.Remove(block);
                }
            }
            else
            {
                _blocks.Remove(previousBytes / BlockBytes);
            }

            return BeginMutation();
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _blocks.Clear();
            _generation = checked(_generation + 1);
        }
    }

    private IDisposable BeginMutation()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _generation = checked(_generation + 1);
        _writers++;
        return new Mutation(this);
    }

    private sealed class Mutation(PhysicalImageCommitment owner) : IDisposable
    {
        private PhysicalImageCommitment? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }

            lock (owner._gate)
            {
                owner._writers--;
                owner._generation = checked(owner._generation + 1);
            }
        }
    }

    /// <summary>Best-effort preparation of unchanged on-disk blocks; never certifies an image.</summary>
    public async ValueTask WarmAsync(CancellationToken token = default)
    {
        await _captureGate.WaitAsync(token).ConfigureAwait(false);
        byte[]? buffer = null;
        try
        {
            await using var file = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
            long length = file.Length;
            long count = length / BlockBytes + (length % BlockBytes == 0 ? 0 : 1);
            for (long block = 0; block < count; block++)
            {
                token.ThrowIfCancellationRequested();
                int bytes = (int)Math.Min(BlockBytes, length - block * BlockBytes);
                long generation;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_closed, this);
                    if (_writers != 0 || _blocks.Count >= MaximumCachedBlocks)
                    {
                        continue;
                    }

                    if (_blocks.TryGetValue(block, out var cached) && cached.Bytes == bytes)
                    {
                        continue;
                    }

                    generation = _generation;
                }
                buffer ??= ArrayPool<byte>.Shared.Rent(BlockBytes);
                int received = 0;
                while (received < bytes)
                {
                    int read = await RandomAccess.ReadAsync(file.SafeFileHandle,
                        buffer.AsMemory(received, bytes - received), block * BlockBytes + received, token).ConfigureAwait(false);
                    // A concurrent truncation only abandons preparation; final capture still validates everything.
                    if (read == 0)
                    {
                        return;
                    }

                    received += read;
                }
                var digest = new BlockDigest(bytes, SHA256.HashData(buffer.AsSpan(0, bytes)));
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_closed, this);
                    if (_writers == 0 && _generation == generation && file.Length == length &&
                        _blocks.Count < MaximumCachedBlocks)
                    {
                        _blocks[block] = digest;
                    }
                }
            }
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            _captureGate.Release();
        }
    }

    /// <summary>Captures the image length and block-based commitment while rejecting overlapping tracked mutations.</summary>
    /// <param name="token">Cancels waiting or reading the image.</param>
    /// <returns>The image length, commitment digest and actual bytes read for this capture.</returns>
    /// <remarks>The digest binds block hashes and image length; it is not the ordinary SHA-256 hash of the file's raw bytes.
    /// Unchanged cached blocks may avoid reads. The owner must prevent untracked external changes.</remarks>
    /// <exception cref="InvalidDataException">A tracked mutation overlaps capture or changes its generation.</exception>
    /// <exception cref="EndOfStreamException">The image ends before the captured length can be read.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested.</exception>
    public async ValueTask<PhysicalImageCommitmentCapture> CaptureAsync(CancellationToken token = default)
    {
        await _captureGate.WaitAsync(token).ConfigureAwait(false);
        byte[]? buffer = null, secondBuffer = null;
        try
        {
            buffer = ArrayPool<byte>.Shared.Rent(BlockBytes);
            secondBuffer = ArrayPool<byte>.Shared.Rent(BlockBytes);
            // Per-block clocks are opt-in; digest bytes, mutation checks and cache
            // admission are identical with and without diagnostic consumers.
            bool measure = PhaseDuration.Enabled;
            long captureStarted = measure ? Stopwatch.GetTimestamp() : 0;
            long phaseStarted = captureStarted;
            long generation;
            Dictionary<long, BlockDigest> cached;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (_writers != 0)
                {
                    throw new InvalidDataException("Physical image capture overlaps a mutation.");
                }

                generation = _generation;
                cached = new(_blocks);
            }
            if (measure)
            {
                RecordPhase("cache_snapshot", Stopwatch.GetTimestamp() - phaseStarted);
            }

            await using var file = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, BlockBytes, FileOptions.Asynchronous | FileOptions.RandomAccess);
            long length = file.Length;
            var handle = file.SafeFileHandle;
            long count = length / BlockBytes + (length % BlockBytes == 0 ? 0 : 1);
            using var root = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            root.AppendData(Domain);
            byte[] header = new byte[sizeof(long) * 2 + sizeof(int)];
            BinaryPrimitives.WriteInt64LittleEndian(header, length);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(sizeof(long)), BlockBytes);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(sizeof(long) + sizeof(int)), count);
            root.AppendData(header);
            var computed = new Dictionary<long, BlockDigest>();
            long readBytes = 0;
            long readHashTicks = 0, readTicks = 0, hashTicks = 0, digestFoldTicks = 0;
            for (long block = 0; block < count; block += 2)
            {
                token.ThrowIfCancellationRequested();
                long batchStarted = measure ? Stopwatch.GetTimestamp() : 0;
                Task<BlockRead> first = ReadBlockAsync(block, buffer);
                Task<BlockRead>? second = block + 1 < count ? ReadBlockAsync(block + 1, secondBuffer) : null;
                // Drain both reads even on failure before disposing the handle or returning buffers.
                if (second is not null)
                {
                    await Task.WhenAll(first, second).ConfigureAwait(false);
                }
                else
                {
                    await first.ConfigureAwait(false);
                }

                if (measure)
                {
                    readHashTicks += Stopwatch.GetTimestamp() - batchStarted;
                }

                Fold(block, first.Result);
                if (second is not null)
                {
                    Fold(block + 1, second.Result);
                }
            }

            Task<BlockRead> ReadBlockAsync(long block, byte[] target)
            {
                int bytes = (int)Math.Min(BlockBytes, length - block * BlockBytes);
                if (cached.TryGetValue(block, out BlockDigest? digest) && digest.Bytes == bytes)
                {
                    return Task.FromResult(new BlockRead(digest, 0, 0, 0));
                }

                return Task.Run(async () =>
                {
                    long started = measure ? Stopwatch.GetTimestamp() : 0;
                    int received = 0;
                    while (received < bytes)
                    {
                        int read = await RandomAccess.ReadAsync(handle,
                            target.AsMemory(received, bytes - received), block * BlockBytes + received, token)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            throw new EndOfStreamException("Physical image ended during capture.");
                        }

                        received += read;
                    }
                    long hashStarted = measure ? Stopwatch.GetTimestamp() : 0;
                    var value = new BlockDigest(bytes, SHA256.HashData(target.AsSpan(0, bytes)));
                    return new BlockRead(value, bytes, measure ? hashStarted - started : 0,
                        measure ? Stopwatch.GetTimestamp() - hashStarted : 0);
                }, token);
            }

            void Fold(long block, BlockRead result)
            {
                readBytes += result.BytesRead;
                readTicks += result.ReadTicks;
                hashTicks += result.HashTicks;
                if (result.BytesRead != 0 && cached.Count + computed.Count < MaximumCachedBlocks)
                {
                    computed.Add(block, result.Digest);
                }

                if (measure)
                {
                    phaseStarted = Stopwatch.GetTimestamp();
                }

                root.AppendData(result.Digest.Hash);
                if (measure)
                {
                    digestFoldTicks += Stopwatch.GetTimestamp() - phaseStarted;
                }
            }
            string hash = Convert.ToHexString(root.GetHashAndReset());
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (_writers != 0 || _generation != generation || file.Length != length)
                {
                    throw new InvalidDataException("Physical state changed during image commitment capture.");
                }

                foreach (var item in computed)
                {
                    _blocks[item.Key] = item.Value;
                }
            }
            if (measure)
            {
                RecordPhase("read_hash", readHashTicks);
                RecordPhase("read", readTicks);
                RecordPhase("hash", hashTicks);
                RecordPhase("digest_fold", digestFoldTicks);
                RecordPhase("total", Stopwatch.GetTimestamp() - captureStarted);
            }
            ImageBytes.Record(length);
            ReadBytes.Record(readBytes);
            return new(length, hash, readBytes);
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            if (secondBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(secondBuffer, clearArray: true);
            }

            _captureGate.Release();
        }
    }

    private sealed record BlockDigest(int Bytes, byte[] Hash);

    private readonly record struct BlockRead(BlockDigest Digest, int BytesRead, long ReadTicks, long HashTicks);
}

/// <summary>Reports a length-bound block commitment and the I/O needed to calculate it.</summary>
/// <param name="Bytes">Total physical image length in bytes.</param>
/// <param name="Sha256">Hexadecimal domain-separated commitment, not a raw-file SHA-256 digest.</param>
/// <param name="BytesRead">Bytes read during this capture; cached blocks may reduce this below the image size.</param>
public readonly record struct PhysicalImageCommitmentCapture(long Bytes, string Sha256, long BytesRead);
