using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntityForge.Db.Distributed.Hosting;

// Values belong to one completed projection. Native immutable values remain
// typed until the transport boundary; decoded values own their JSON storage.
[JsonConverter(typeof(PartitionProjectionValue.Converter))]
internal sealed class PartitionProjectionValue(object? value, JsonSerializerOptions? options = null)
{
    internal interface IWriter
    {
        void Write(Utf8JsonWriter writer);
    }

    internal object? Native => value;

    // Locally calculated for an owned immutable query page, never peer supplied.
    internal long? QueryPageBytes
    {
        get; init;
    }

    // Never mutated after publication; only attached to fresh reply/control envelopes.
    internal QueryWire.Page? OperatorWirePage
    {
        get; init;
    }

    internal JsonElement Element => value is IWriter ? JsonSerializer.Deserialize<JsonElement>(Encode()) : value is RawEvidence raw ? JsonSerializer.Deserialize<JsonElement>(raw.Bytes) : value is JsonElement element ? element :
        JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), options ?? PartitionParticipantWire.Json);

    internal byte[] Encode(int maximum = PartitionParticipantWire.MaximumResponseBytes)
    {
        using var output = new PooledOutput(maximum, retain: true);
        using (var writer = new Utf8JsonWriter(output))
        {
            Write(writer);
        }

        return output.ToArray();
    }

    internal byte[] EncodeProtocol(int maximum = PartitionParticipantWire.MaximumResponseBytes) => value switch
    {
        byte[] bytes when bytes.Length <= maximum => bytes.ToArray(),
        byte[] => throw new InvalidDataException("Projection exceeds its transport budget."),
        PartitionValidatedRead read => PartitionParticipantBinary.Encode(read, maximum),
        _ => Encode(maximum)
    };

    internal static PartitionProjectionValue Decode(byte[] bytes) => new(JsonSerializer.Deserialize<JsonElement>(bytes));

    internal static PartitionProjectionValue Preserve(byte[] bytes) => new(new RawEvidence(bytes));

    internal static long Measure(object value, int maximum, JsonSerializerOptions? options = null)
    {
        using var output = new PooledOutput(maximum, retain: false);
        using (var writer = new Utf8JsonWriter(output))
        {
            new PartitionProjectionValue(value, options).Write(writer);
        }

        return output.Length;
    }

    private sealed record RawEvidence(byte[] Bytes);

    internal void Write(Utf8JsonWriter writer)
    {
        if (value is IWriter owned)
        {
            owned.Write(writer);
        }
        else if (value is RawEvidence raw)
        {
            writer.WriteRawValue(raw.Bytes);
        }
        else
        {
            JsonSerializer.Serialize(writer, value, value?.GetType() ?? typeof(object), options ?? PartitionParticipantWire.Json);
        }
    }

    internal sealed class Converter : JsonConverter<PartitionProjectionValue>
    {
        public override bool HandleNull => true;

        public override PartitionProjectionValue Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            new(JsonSerializer.Deserialize<JsonElement>(ref reader));

        public override void Write(Utf8JsonWriter writer, PartitionProjectionValue value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                value.Write(writer);
            }
        }
    }

    // Utf8JsonWriter(Stream) allocates an ArrayBufferWriter internally even for
    // a counting stream. Supply pooled segments directly; counting may discard a
    // segment only after Advance transfers ownership back to this writer.
    private sealed class PooledOutput(int maximum, bool retain) : IBufferWriter<byte>, IDisposable
    {
        private const int MinimumSegmentBytes = 256;
        private const int JsonEscapeBytesPerCharacter = 6;
        private const int WriterGrowthAllowance = 4096;
        private byte[]? _buffer;
        private int _offset;

        internal long Length
        {
            get; private set;
        }

        internal byte[] ToArray() => retain ? _buffer.AsSpan(0, _offset).ToArray() : throw new InvalidOperationException();

        public void Advance(int count)
        {
            if (count < 0 || _buffer is null || count > _buffer.Length - _offset)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (count > maximum - Length)
            {
                throw new InvalidOperationException("Projection exceeds its transport budget.");
            }

            Length += count;
            if (retain)
            {
                _offset += count;
            }
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            // JSON escaping can reserve more bytes than it eventually writes.
            // Bound that reservation separately from the strict emitted-byte cap.
            if (sizeHint < 0 || sizeHint > checked((long)maximum * JsonEscapeBytesPerCharacter + WriterGrowthAllowance))
            {
                throw new InvalidOperationException("Projection exceeds its encoding budget.");
            }

            int required = checked(_offset + Math.Max(sizeHint, MinimumSegmentBytes));
            if (_buffer is null || _buffer.Length < required)
            {
                byte[] next = ArrayPool<byte>.Shared.Rent(required);
                if (_buffer is not null)
                {
                    if (retain)
                    {
                        _buffer.AsSpan(0, _offset).CopyTo(next);
                    }

                    ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
                }
                _buffer = next;
            }
            return _buffer.AsMemory(_offset);
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public void Dispose()
        {
            if (_buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            }

            _buffer = null;
        }
    }
}
