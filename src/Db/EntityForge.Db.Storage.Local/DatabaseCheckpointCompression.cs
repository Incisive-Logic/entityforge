using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace EntityForge.Db.Storage.Local;

/// <summary>Checksummed, bounded streaming compression shared by native profile checkpoints.</summary>
internal static class DatabaseCheckpointCompression
{
    private const int MagicLength = 4;
    private const byte FrameVersion = 1;
    private const int HeaderLength = 9;
    private const int LengthOffset = 5;
    private const int ChecksumLength = 32;
    private const int WorkChunkBytes = 64 * 1024;
    internal const int MaximumDecodedBytes = 512 * 1024 * 1024;
    private const int MaximumFrameBytes = MaximumDecodedBytes;

    internal static ReadOnlyMemory<byte> Encode(ReadOnlyMemory<byte> payload, ReadOnlySpan<byte> magic,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (payload.Length > MaximumDecodedBytes)
        {
            throw new InvalidDataException("profile checkpoint exceeds its expanded byte bound.");
        }

        return Encode(stream => stream.Write(payload.Span), magic, cancellationToken);
    }

    internal static ReadOnlyMemory<byte> Encode(Action<Stream> writePayload, ReadOnlySpan<byte> magic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writePayload);
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream();
        if (magic.Length != MagicLength)
        {
            throw new ArgumentException("Checkpoint magic must contain four bytes.", nameof(magic));
        }

        stream.Write(magic);
        stream.WriteByte(FrameVersion);
        Span<byte> length = stackalloc byte[sizeof(int)];
        length.Clear();
        stream.Write(length);
        long expandedLength;
        using (var encoder = new BrotliStream(stream, CompressionLevel.Fastest, leaveOpen: true))
        {
            using var sink = new ChunkedEncodingStream(encoder, stream, cancellationToken);
            writePayload(sink);
            sink.Flush();
            expandedLength = sink.Length;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.Length > MaximumFrameBytes - ChecksumLength)
        {
            throw new InvalidDataException("Compressed profile checkpoint exceeds its byte bound.");
        }

        BinaryPrimitives.WriteInt32LittleEndian(stream.GetBuffer().AsSpan(LengthOffset, sizeof(int)),
            checked((int)expandedLength));
        byte[] checksum = SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        stream.Write(checksum);
        return stream.GetBuffer().AsMemory(0, checked((int)stream.Length));
    }

    private sealed class ChunkedEncodingStream(Stream encoder, MemoryStream frame,
        CancellationToken cancellationToken) : Stream
    {
        private readonly byte[] _buffer = new byte[WorkChunkBytes];
        private int _buffered;
        private long _length;
        private bool _closed;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !_closed;

        public override long Length => _length;

        public override long Position
        {
            get => _length; set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void WriteByte(byte value)
        {
            Span<byte> single = stackalloc byte[1];
            single[0] = value;
            Write(single);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length > MaximumDecodedBytes - _length)
            {
                throw new InvalidDataException("profile checkpoint exceeds its expanded byte bound.");
            }

            _length += buffer.Length;
            while (!buffer.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(buffer.Length, WorkChunkBytes - _buffered);
                buffer[..count].CopyTo(_buffer.AsSpan(_buffered));
                _buffered += count;
                buffer = buffer[count..];
                if (_buffered == WorkChunkBytes)
                {
                    Flush();
                }
            }
        }

        public override void Flush()
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_buffered == 0)
            {
                return;
            }

            encoder.Write(_buffer.AsSpan(0, _buffered));
            _buffered = 0;
            if (frame.Length > MaximumFrameBytes - ChecksumLength)
            {
                throw new InvalidDataException("Compressed profile checkpoint exceeds its byte bound.");
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _closed = true;
            base.Dispose(disposing);
        }
    }

    internal static byte[] Decode(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> magic, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (magic.Length != MagicLength || frame.Length <= HeaderLength + ChecksumLength || frame.Length > MaximumFrameBytes ||
            !frame[..magic.Length].SequenceEqual(magic) || frame[magic.Length] != FrameVersion)
        {
            throw new InvalidDataException("Invalid compressed profile checkpoint header or size.");
        }

        int authenticatedLength = frame.Length - ChecksumLength;
        Span<byte> checksum = stackalloc byte[ChecksumLength];
        SHA256.HashData(frame[..authenticatedLength], checksum);
        if (!CryptographicOperations.FixedTimeEquals(checksum, frame[authenticatedLength..]))
        {
            throw new InvalidDataException("Compressed profile checkpoint checksum is invalid.");
        }
        // Authenticate the declared expansion size before allocating or decoding any payload.
        int decodedLength = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(LengthOffset, sizeof(int)));
        if (decodedLength < 0 || decodedLength > MaximumDecodedBytes)
        {
            throw new InvalidDataException("Compressed profile checkpoint expansion exceeds its byte bound.");
        }

        byte[] decoded = new byte[decodedLength];
        ReadOnlySpan<byte> compressed = frame.Slice(HeaderLength, authenticatedLength - HeaderLength);
        using var decoder = new BrotliDecoder();
        int read = 0, written = 0;
        // Brotli may need output space to consume its end marker, even for an empty payload.
        // Any byte produced beyond the authenticated length is rejected rather than retained.
        Span<byte> overflow = stackalloc byte[1];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OperationStatus status = decoder.Decompress(
                compressed.Slice(read, Math.Min(WorkChunkBytes, compressed.Length - read)),
                written == decoded.Length ? overflow :
                    decoded.AsSpan(written, Math.Min(WorkChunkBytes, decoded.Length - written)),
                out int consumed, out int produced);
            if (produced > decoded.Length - written)
            {
                throw new InvalidDataException("Compressed profile checkpoint exceeds its declared length.");
            }

            read += consumed;
            written += produced;
            if (status == OperationStatus.Done)
            {
                if (read != compressed.Length || written != decoded.Length)
                {
                    throw new InvalidDataException("Compressed profile checkpoint has trailing input or a length mismatch.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                return decoded;
            }
            if (status == OperationStatus.InvalidData || (consumed == 0 && produced == 0) ||
                (status == OperationStatus.NeedMoreData && read == compressed.Length))
            {
                throw new InvalidDataException("Compressed profile checkpoint is truncated or exceeds its declared length.");
            }
        }
    }
}
