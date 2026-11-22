using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Pages;

// Initialized WAL container. The logical payload remains the existing redo format.
internal sealed class InitializedRedoStream : Stream
{
    internal const int BlockBytes = 4096;
    internal const int HeaderBytes = 5 * BlockBytes;
    internal const int RecordBytes = 128;
    internal const int DefaultExtentBytes = 8 * 1024 * 1024;
    private const int HashOffset = RecordBytes - 32;
    private const int CopyCount = 4;
    private const int InitializationBufferBytes = 1024 * 1024;
    // Private, never mutated or exposed: independent files may read these zeros concurrently.
    private static readonly ReadOnlyMemory<byte> InitializationZeros = new byte[InitializationBufferBytes];
    private const byte ContainerVersion = 10;
    private readonly FileStream _file;
    private readonly DirectorySession _directorySession;
    private readonly Guid _identity;
    private readonly int _extentBytes;
    private readonly IRedoDurabilityObserver _observer;
    private long _length, _position, _sequence, _capacity;
    private bool _disposed;

    private InitializedRedoStream(FileStream file, Guid identity, int extentBytes, Tail tail,
        IRedoDurabilityObserver observer, DirectorySession directorySession)
    {
        _file = file;
        _identity = identity;
        _extentBytes = extentBytes;
        _sequence = tail.Sequence;
        _length = tail.End;
        _capacity = tail.Capacity;
        _observer = observer;
        _directorySession = directorySession;
    }

    // Proof belongs to this successfully opened directory lineage, never a process-global
    // cache. A fresh/recovery open always reconciles ancestors, including interrupted creation.
    private sealed class DirectorySession(string path)
    {
        private bool prepared;

        internal void EnsurePrepared(string directory)
        {
            if (!string.Equals(path, directory, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new ArgumentException("A replacement WAL must remain in its established directory.");
            }

            if (prepared && Directory.Exists(path))
            {
                return;
            }

            CreateDirectoryDurably(path);
            prepared = true;
        }

        internal void Invalidate() => prepared = false;
    }

    internal static Task<InitializedRedoStream> OpenAsync(string path,
        int extentBytes = DefaultExtentBytes, CancellationToken cancellationToken = default,
        IRedoDurabilityObserver? observer = null) =>
        OpenInDirectoryAsync(path, extentBytes, cancellationToken, observer,
            new DirectorySession(Path.GetDirectoryName(Path.GetFullPath(path))!));

    // May also reopen the selected name after this file handle was closed for replacement.
    // Only the directory proof is reused; every file and publication barrier remains intact.
    internal Task<InitializedRedoStream> OpenSiblingAsync(string path, CancellationToken cancellationToken = default,
        RedoDurabilityTelemetry? telemetry = null) =>
        OpenInDirectoryAsync(path, _extentBytes, cancellationToken, _observer, _directorySession, telemetry);

    private static async Task<InitializedRedoStream> OpenInDirectoryAsync(string path, int extentBytes,
        CancellationToken cancellationToken, IRedoDurabilityObserver? observer, DirectorySession directorySession,
        RedoDurabilityTelemetry? telemetry = null)
    {
        try
        {
            return await OpenCoreAsync(path, extentBytes, cancellationToken, observer, directorySession, telemetry).ConfigureAwait(false);
        }
        catch
        {
            directorySession.Invalidate();
            throw;
        }
    }

    private static async Task<InitializedRedoStream> OpenCoreAsync(string path, int extentBytes,
        CancellationToken cancellationToken, IRedoDurabilityObserver? observer, DirectorySession directorySession, RedoDurabilityTelemetry? telemetry)
    {
        long phaseStarted = telemetry?.MeasureInitialization == true ? Stopwatch.GetTimestamp() : 0;
        if (extentBytes < BlockBytes || extentBytes % BlockBytes != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extentBytes));
        }

        path = Path.GetFullPath(path);
        directorySession.EnsurePrepared(Path.GetDirectoryName(path)!);
        if (phaseStarted != 0)
        {
            phaseStarted = telemetry!.InitializationPhase("directory_prepare", phaseStarted);
        }

        if (!File.Exists(path))
        {
            // Never publish partially initialized capacity as an empty database.
            string temporary = path + ".initializing";
            await using (var initial = OpenFile(temporary, FileMode.Create))
            {
                Guid identity = Guid.NewGuid();
                await FillAsync(initial, 0, HeaderBytes + (long)extentBytes, cancellationToken).ConfigureAwait(false);
                byte[] header = Header(identity);
                initial.Position = 0;
                await initial.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                byte[] empty = EncodeTail(identity, new(0, 0, extentBytes));
                for (int i = 0; i < CopyCount; i++)
                {
                    initial.Position = SlotOffset(i);
                    await initial.WriteAsync(empty, cancellationToken).ConfigureAwait(false);
                }
                if (phaseStarted != 0)
                {
                    phaseStarted = telemetry!.InitializationPhase("extent_and_header_write", phaseStarted);
                }

                await initial.FlushAsync(cancellationToken).ConfigureAwait(false);
                DurableFile.SyncData(initial.SafeFileHandle);
                if (phaseStarted != 0)
                {
                    phaseStarted = telemetry!.InitializationPhase("initial_file_flush", phaseStarted);
                }
            }
            File.Move(temporary, path, false);
            if (phaseStarted != 0)
            {
                phaseStarted = telemetry!.InitializationPhase("initial_rename", phaseStarted);
            }
        }
        FileStream file = OpenFile(path, FileMode.Open);
        try
        {
            byte[] header = new byte[RecordBytes];
            await file.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan(0, 4).SequenceEqual("EFRW"u8) || header[4] != ContainerVersion || !ValidHash(header))
            {
                throw Corrupt("Invalid initialized-WAL header.");
            }

            Guid identity = new(header.AsSpan(8, 16));
            if (identity == Guid.Empty)
            {
                throw Corrupt("Missing initialized-WAL identity.");
            }

            var copies = new Tail?[CopyCount];
            for (int i = 0; i < copies.Length; i++)
            {
                byte[] bytes = new byte[RecordBytes];
                file.Position = SlotOffset(i);
                await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                copies[i] = DecodeTail(bytes, identity, file.Length);
            }
            Tail selected = Select(copies);
            bool repairedCapacity = false;
            if (file.Length > HeaderBytes + selected.Capacity)
            {
                file.SetLength(HeaderBytes + selected.Capacity);
                repairedCapacity = true;
            }
            // Repair only a mirror with independently validated current evidence.
            // Never fall back to a predecessor when the newer bank is ambiguous.
            bool repaired = repairedCapacity;
            for (int i = 0; i < copies.Length; i++)
            {
                Tail mate = copies[i ^ 1] ?? selected;
                if (copies[i] is null || (mate.Sequence == selected.Sequence && copies[i]!.Sequence < mate.Sequence))
                {
                    file.Position = SlotOffset(i);
                    await file.WriteAsync(EncodeTail(identity, selected), cancellationToken).ConfigureAwait(false);
                    repaired = true;
                }
            }
            if (repaired)
            {
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                DurableFile.SyncData(file.SafeFileHandle);
            }
            // Also retries an earlier uncertain rename/directory-sync result before
            // any new append may be acknowledged through this reopened instance.
            if (phaseStarted != 0)
            {
                phaseStarted = telemetry!.InitializationPhase("validation_and_repair", phaseStarted);
            }

            SyncDirectory(Path.GetDirectoryName(path)!);
            if (phaseStarted != 0)
            {
                telemetry!.InitializationPhase("publication_directory_sync", phaseStarted);
            }

            return new(file, identity, extentBytes, selected, observer ?? new NoRedoDurabilityObserver(), directorySession);
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask CommitAsync(CancellationToken cancellationToken = default, RedoDurabilityTelemetry? telemetry = null)
    {
        bool measure = telemetry?.MeasurePhases == true;
        long started = measure ? Stopwatch.GetTimestamp() : 0;
        long allocated = measure ? GC.GetTotalAllocatedBytes() : 0;
        await _file.FlushAsync(cancellationToken).ConfigureAwait(false);
        var next = new Tail(checked(_sequence + 1), _length, _capacity);
        byte[] tail = EncodeTail(_identity, next);
        int bank = (int)(next.Sequence % 2) * 2;
        _observer.Reached(RedoDurabilityStage.BeforeTailPublication);
        for (int i = bank; i < bank + 2; i++)
        {
            _file.Position = SlotOffset(i);
            await _file.WriteAsync(tail, cancellationToken).ConfigureAwait(false);
            if (i == bank)
            {
                await _file.FlushAsync(cancellationToken).ConfigureAwait(false);
                _observer.Reached(RedoDurabilityStage.AfterFirstTailCopy);
            }
        }
        await _file.FlushAsync(cancellationToken).ConfigureAwait(false);
        _observer.Reached(RedoDurabilityStage.AfterTailPublication);
        if (measure)
        {
            telemetry!.Phase("wal_tail_publication", started, allocated);
            started = Stopwatch.GetTimestamp();
            allocated = GC.GetTotalAllocatedBytes();
        }
        // This interval contains the synchronous native sync call, without an async
        // continuation wait. It can still include OS preemption or CPU throttling.
        DurableFile.SyncData(_file.SafeFileHandle);
        if (measure)
        {
            telemetry!.Phase("wal_data_sync", started, allocated);
        }

        _sequence = next.Sequence;
    }

    private async ValueTask EnsureCapacityAsync(long required, CancellationToken token)
    {
        if (required <= _capacity)
        {
            return;
        }

        long capacity = checked(((required + _extentBytes - 1) / _extentBytes) * _extentBytes);
        _observer.Reached(RedoDurabilityStage.BeforeCapacityInitialization);
        await _file.FlushAsync(token).ConfigureAwait(false);
        await FillAsync(_file, HeaderBytes + _capacity, HeaderBytes + capacity, token).ConfigureAwait(false);
        await _file.FlushAsync(token).ConfigureAwait(false);
        DurableFile.SyncData(_file.SafeFileHandle); // Fully initialize newly allocated extents before use.
        _capacity = capacity;
        _observer.Reached(RedoDurabilityStage.AfterCapacityInitialization);
    }

    private static async Task FillAsync(FileStream file, long start, long end, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (end > start && OperatingSystem.IsLinux())
        {
            // Flush managed buffers before comparing the native EOF. Allocation is
            // restricted to a new tail, so stale existing bytes can never survive.
            await file.FlushAsync(token).ConfigureAwait(false);
            if (DurableFile.TryAllocateZeroedTail(file.SafeFileHandle, start, end - start, token))
            {
                file.Position = end;
                return;
            }
        }
        ReadOnlyMemory<byte> zeros = InitializationZeros;
        file.Position = start;
        while (file.Position < end)
        {
            await file.WriteAsync(zeros[..(int)Math.Min(zeros.Length, end - file.Position)], token).ConfigureAwait(false);
        }
    }

    internal sealed record Tail(long Sequence, long End, long Capacity);

    internal static Tail Select(IReadOnlyList<Tail?> copies)
    {
        if (copies.Count != CopyCount)
        {
            throw Corrupt("Missing tail mirrors.");
        }

        Tail selected = copies.Where(x => x is not null).OrderByDescending(x => x!.Sequence).FirstOrDefault()
            ?? throw Corrupt("No valid logical end.");
        for (int i = 0; i < copies.Count; i++)
        {
            Tail? value = copies[i];
            if (value is null)
            {
                if (copies[i ^ 1] != selected)
                {
                    throw Corrupt("Ambiguous damaged tail bank.");
                }
            }
            else if ((value.Sequence == selected.Sequence && value != selected) ||
                     value.Sequence < selected.Sequence - 2 || value.End > selected.End || value.Capacity > selected.Capacity)
            {
                throw Corrupt("Inconsistent logical ends.");
            }
        }
        return selected;
    }

    private static byte[] Header(Guid identity)
    {
        byte[] bytes = new byte[RecordBytes];
        "EFRW"u8.CopyTo(bytes);
        bytes[4] = ContainerVersion;
        identity.TryWriteBytes(bytes.AsSpan(8, 16));
        Seal(bytes);
        return bytes;
    }

    internal static byte[] EncodeTail(Guid identity, Tail tail)
    {
        byte[] bytes = new byte[RecordBytes];
        "EFIT"u8.CopyTo(bytes);
        bytes[4] = ContainerVersion;
        identity.TryWriteBytes(bytes.AsSpan(8, 16));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), tail.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), tail.End);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(40), tail.Capacity);
        Seal(bytes);
        return bytes;
    }

    private static Tail? DecodeTail(byte[] bytes, Guid identity, long physicalLength)
    {
        if (!bytes.AsSpan(0, 4).SequenceEqual("EFIT"u8) || bytes[4] != ContainerVersion || !ValidHash(bytes) ||
            new Guid(bytes.AsSpan(8, 16)) != identity)
        {
            return null;
        }

        var result = new Tail(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24)),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(32)), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(40)));
        if (result.Sequence < 0 || result.End < 0 || result.Capacity < result.End ||
            result.Capacity < BlockBytes || result.Capacity > physicalLength - HeaderBytes)
        {
            return null;
        }

        return result;
    }

    private static void Seal(byte[] bytes) => SHA256.HashData(bytes.AsSpan(0, HashOffset), bytes.AsSpan(HashOffset));

    private static bool ValidHash(byte[] bytes) => SHA256.HashData(bytes.AsSpan(0, HashOffset)).AsSpan().SequenceEqual(bytes.AsSpan(HashOffset));

    internal static long SlotOffset(int index) => (index + 1L) * BlockBytes;

    private static RedoWalCorruptionException Corrupt(string message) => new(message);

    private static FileStream OpenFile(string path, FileMode mode) => new(path, mode, FileAccess.ReadWrite,
        FileShare.Read, BlockBytes, FileOptions.Asynchronous);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position != _length)
        {
            throw new InvalidOperationException("WAL data is append-only.");
        }

        await EnsureCapacityAsync(checked(_position + buffer.Length), cancellationToken).ConfigureAwait(false);
        _file.Position = HeaderBytes + _position;
        await _file.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _position += buffer.Length;
        _length = _position;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int count = (int)Math.Min(buffer.Length, Math.Max(0, _length - _position));
        _file.Position = HeaderBytes + _position;
        int read = await _file.ReadAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() => _file.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _file.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin)
    {
        long position = checked((origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => _position,
            SeekOrigin.End => _length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        }) + offset);
        if (position < 0)
        {
            throw new IOException("Negative position.");
        }

        return _position = position;
    }

    public override void SetLength(long value) => throw new NotSupportedException("Committed boundaries cannot be truncated.");

    public override bool CanRead => !_disposed;

    public override bool CanSeek => !_disposed;

    public override bool CanWrite => !_disposed;

    public override long Length => _length;

    internal long PhysicalLength => _file.Length;

    // Capture only while the writer is excluded and its buffers are flushed. The
    // append-only payload below this boundary cannot change as new groups arrive.
    // This handle has its own position and is closed before generation replacement.
    internal Stream OpenReadRange(long start, long end)
    {
        if (start < 0 || end < start || end > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        return new PayloadRange(_file.Name, start, end - start);
    }

    private sealed class PayloadRange : Stream
    {
        private const int ReadAheadBytes = 64 * 1024;
        private readonly FileStream file;
        private readonly long start, length;
        private long position;

        internal PayloadRange(string path, long start, long length)
        {
            this.start = start;
            this.length = length;
            file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                ReadAheadBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            file.Position = HeaderBytes + start;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            int count = (int)Math.Min(buffer.Length, Math.Max(0, length - position));
            int read = await file.ReadAsync(buffer[..count], token).ConfigureAwait(false);
            position += read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override long Seek(long offset, SeekOrigin origin)
        {
            long next = checked((origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => position,
                SeekOrigin.End => length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            }) + offset);
            if (next < 0 || next > length)
            {
                throw new IOException("Position outside captured WAL range.");
            }

            if (next != position)
            {
                file.Position = HeaderBytes + start + next;
            }

            return position = next;
        }

        public override long Position
        {
            get => position; set => Seek(value, SeekOrigin.Begin);
        }

        public override long Length => length;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                file.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await file.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    public override long Position
    {
        get => _position; set => Seek(value, SeekOrigin.Begin);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file.Dispose();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _file.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    internal static void SyncDirectory(string path) => DurableDirectory.Sync(path);

    private static void CreateDirectoryDurably(string path) => DurableDirectory.Create(path);
}
