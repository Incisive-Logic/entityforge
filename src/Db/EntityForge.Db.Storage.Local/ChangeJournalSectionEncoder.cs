using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace EntityForge.Db.Storage.Local;

// Batch-owned scratch buffers; only the returned compressed section escapes this lifetime.
internal sealed class ChangeJournalSectionEncoder : IDisposable
{
    private const int BufferBytes = 64 * 1024;
    private const int MaximumStringBytes = ChangeJournalBinaryCodec.MaximumStringBytes;
    private const int GuidBytes = 16;
    private bool _disposed;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
    private readonly byte[] _text = ArrayPool<byte>.Shared.Rent(MaximumStringBytes);
    private readonly MemoryStream _output = new();

    internal byte[] Record(CommittedChangeSet change, CancellationToken token) => Encode(
        ChangeJournalBinaryCodec.RecordVersion, writer =>
        {
            writer.Guid(change.DatabaseIncarnation);
            writer.Int64(change.Version.Value);
            writer.Guid(change.TransactionId);
            writer.Byte((byte)change.Profile);
            writer.String(change.Cursor.Value);
            var payload = change.Payload;
            writer.String(payload.CodecId);
            writer.Int32(payload.CodecVersion);
            if (payload.Content.Length > DatabaseChangeFeedLimits.MaximumCommittedChangePayloadBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(change), "A committed-change payload exceeds the supported size.");
            }

            writer.Int32(payload.Content.Length);
            writer.Write(payload.Content.Span);
            writer.Int32(payload.EffectCount);
            writer.Byte((byte)payload.Kind);
            writer.Routing(payload.Routing);
        }, token);

    internal byte[] Index(DatabaseProfileId profile, CommittedChangeRoutingSummary routing, CancellationToken token) =>
        Encode(ChangeJournalBinaryCodec.IndexVersion, writer =>
        {
            writer.Byte((byte)profile);
            writer.Routing(routing);
        }, token);

    private byte[] Encode(int version, Action<Writer> encode, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        _output.SetLength(0);
        _output.Position = 0;
        Span<byte> header = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, version);
        _output.Write(header);
        using (var compressed = new BrotliStream(_output, CompressionLevel.Fastest, leaveOpen: true))
        {
            var writer = new Writer(compressed, _buffer, _text, token);
            encode(writer);
            writer.Flush();
        }
        token.ThrowIfCancellationRequested();
        return _output.ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _output.Dispose();
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        ArrayPool<byte>.Shared.Return(_text, clearArray: true);
    }

    private sealed class Writer(Stream output, byte[] buffer, byte[] text, CancellationToken token)
    {
        private int _length;

        internal void Write(ReadOnlySpan<byte> value)
        {
            while (!value.IsEmpty)
            {
                token.ThrowIfCancellationRequested();
                int size = Math.Min(BufferBytes - _length, value.Length);
                value[..size].CopyTo(buffer.AsSpan(_length));
                _length += size;
                value = value[size..];
                if (_length == BufferBytes)
                {
                    Flush();
                }
            }
        }

        internal void Flush()
        {
            token.ThrowIfCancellationRequested();
            if (_length == 0)
            {
                return;
            }

            output.Write(buffer, 0, _length);
            _length = 0;
        }

        internal void Byte(byte value)
        {
            Span<byte> bytes = stackalloc byte[1];
            bytes[0] = value;
            Write(bytes);
        }

        internal void Int32(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            Write(bytes);
        }

        internal void Int64(long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            Write(bytes);
        }

        internal void Guid(Guid value)
        {
            Span<byte> bytes = stackalloc byte[GuidBytes];
            value.TryWriteBytes(bytes);
            Write(bytes);
        }

        internal void String(string value)
        {
            token.ThrowIfCancellationRequested();
            int count = Utf8.GetByteCount(value);
            if (count > MaximumStringBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "A change-journal string exceeds the supported size.");
            }

            Int32(count);
            Utf8.GetBytes(value.AsSpan(), text.AsSpan(0, count));
            Write(text.AsSpan(0, count));
        }

        internal void Routing(CommittedChangeRoutingSummary routing)
        {
            Int32(routing.Keys.Count);
            foreach (var key in routing.Keys)
            {
                String(key.Kind);
                String(key.Value);
            }
            Int32(routing.OverflowedKinds.Count);
            foreach (string kind in routing.OverflowedKinds)
            {
                String(kind);
            }
        }
    }
}
