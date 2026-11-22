using System.IO.Compression;
using System.Buffers;
using System.Text.Json;

namespace EntityForge.Db.Storage.Local;

internal static class ChangeJournalManifestCodec
{
    private const int SerializationBufferBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Options = new() { DefaultBufferSize = SerializationBufferBytes };
    private static readonly byte[] Header = "EFCM4"u8.ToArray();
    private static readonly byte[] End = "EFCEND"u8.ToArray();

    internal static async ValueTask WriteAsync(Stream output, ChangeJournalManifest manifest, CancellationToken token)
    {
        await output.WriteAsync(Header, token).ConfigureAwait(false);
        await using (var compressed = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            await JsonSerializer.SerializeAsync(compressed, manifest, Options, token).ConfigureAwait(false);
        }

        await output.WriteAsync(End, token).ConfigureAwait(false);
    }

    internal static byte[] Write(ChangeJournalManifest manifest)
    {
        using var output = new MemoryStream();
        output.Write(Header);
        using (var compressed = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            JsonSerializer.Serialize(compressed, manifest, Options);
        }

        output.Write(End);
        return output.ToArray();
    }

    internal static ChangeJournalManifest Read(byte[] payload)
    {
        if (payload.Length <= Header.Length + End.Length || !payload.AsSpan().StartsWith(Header) ||
            !payload.AsSpan().EndsWith(End))
        {
            throw new EntityForgeDbFormatException("The compressed journal catalog framing is invalid.");
        }

        try
        {
            using var compressed = new CatalogReadStream(payload.AsMemory(Header.Length,
                payload.Length - Header.Length - End.Length));
            var manifest = JsonSerializer.Deserialize<ChangeJournalManifest>(compressed) ??
                throw new EntityForgeDbFormatException("The journal catalog is empty.");
            if (compressed.ReadByte() != -1)
            {
                throw new EntityForgeDbFormatException("The journal catalog has trailing data.");
            }

            return manifest;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            throw new EntityForgeDbFormatException("The compressed journal catalog is invalid.", exception);
        }
    }

    // Decode without a second whole-catalog array and require exact compressed framing.
    private sealed class CatalogReadStream(ReadOnlyMemory<byte> input) : Stream
    {
        private BrotliDecoder _decoder;
        private int _offset;
        private long _decoded;
        private bool _done;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_done || buffer.IsEmpty)
            {
                return 0;
            }

            OperationStatus status = _decoder.Decompress(input.Span[_offset..], buffer,
                out int consumed, out int written);
            _offset += consumed;
            _decoded += written;
            // Preserve the old byte-array reader's maximum addressable payload.
            if (_decoded > Array.MaxLength)
            {
                throw new InvalidDataException("The decoded journal catalog exceeds the supported size.");
            }

            if (status == OperationStatus.Done)
            {
                if (_offset != input.Length)
                {
                    throw new InvalidDataException("The journal catalog has trailing compressed data.");
                }

                _done = true;
            }
            else if (status != OperationStatus.DestinationTooSmall || written == 0)
            {
                throw new InvalidDataException("The compressed journal catalog is invalid or incomplete.");
            }

            return written;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _decoder.Dispose();
            }

            base.Dispose(disposing);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _decoded; set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
