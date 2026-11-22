namespace EntityForge.Db;

// Bounds expanded checkpoint work without changing a profile's wire format.
// The caller owns the underlying stream; cancellation never flushes partial work.
internal sealed class DatabaseCheckpointStream(Stream stream, long maximumBytes,
    CancellationToken cancellationToken) : Stream
{
    private const int ChunkBytes = 64 * 1024;
    private long _processed;
    private bool _disposed;

    public override bool CanRead => !_disposed && stream.CanRead;

    public override bool CanWrite => !_disposed && stream.CanWrite;

    public override bool CanSeek => false;

    public override long Length => _processed;

    public override long Position
    {
        get => _processed; set => throw new NotSupportedException();
    }

    private void Check()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        Check();
        if (buffer.IsEmpty)
        {
            return 0;
        }

        int count = (int)Math.Min(buffer.Length, Math.Min(ChunkBytes, maximumBytes - _processed + 1));
        int read = stream.Read(buffer[..count]);
        _processed += read;
        if (_processed > maximumBytes)
        {
            throw new InvalidDataException("Checkpoint exceeds its expanded byte bound.");
        }

        Check();
        return read;
    }

    public override int ReadByte()
    {
        Span<byte> b = stackalloc byte[1];
        return Read(b) == 0 ? -1 : b[0];
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Check();
        if (buffer.Length > maximumBytes - _processed)
        {
            throw new InvalidDataException("Checkpoint exceeds its expanded byte bound.");
        }

        while (!buffer.IsEmpty)
        {
            Check();
            int count = Math.Min(ChunkBytes, buffer.Length);
            stream.Write(buffer[..count]);
            _processed += count;
            buffer = buffer[count..];
        }
    }

    public override void WriteByte(byte value)
    {
        Span<byte> b = stackalloc byte[1];
        b[0] = value;
        Write(b);
    }

    public override void Flush()
    {
        Check();
        stream.Flush();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }
}
