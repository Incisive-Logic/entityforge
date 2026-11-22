using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntityForge.Db.Distributed.Hosting;

// Shared participant framing: small typed control plus owned opaque native bytes.
// A payload is never converted to base64, and admission/checksum precede allocation.
internal static class PartitionParticipantBinary
{
    private static ReadOnlySpan<byte> Magic => "EFPW0001"u8;

    private const int HashBytes = SHA256.HashSizeInBytes;
    private const int ControlLengthOffset = 8; // Eight-byte version discriminator.
    private const int PartCountOffset = ControlLengthOffset + sizeof(int);
    private const int HeaderBytes = PartCountOffset + sizeof(int);
    private const int MaximumParts = 65536;
    internal const string ContentType = "application/vnd.entityforge.partition";

    internal static bool IsBinary(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Magic);

    // Serialization is synchronous. Reuse type metadata while keeping payload
    // ownership invocation-local, including nested calls on the same thread.
    [ThreadStatic] private static Parts? _parts;
    private static readonly JsonSerializerOptions Json = CreateOptions();

    private sealed class Scope(Parts parts) : IDisposable
    {
        private readonly Parts? _previous = Exchange(parts);

        private static Parts? Exchange(Parts value)
        {
            var prior = _parts;
            _parts = value;
            return prior;
        }

        public void Dispose() => _parts = _previous;
    }

    internal static byte[] Encode<T>(T value, int maximum = PartitionParticipantWire.MaximumRequestBytes)
    {
        var parts = new Parts(maximum);
        using var scope = new Scope(parts);
        byte[] control = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        long length = HeaderBytes + (long)control.Length + parts.Bytes + sizeof(int) * (long)parts.Values.Count + HashBytes;
        if (length > maximum)
        {
            throw new InvalidDataException("Partition frame exceeds its bound.");
        }

        byte[] result = new byte[(int)length];
        Magic.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(ControlLengthOffset), control.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(PartCountOffset), parts.Values.Count);
        control.CopyTo(result.AsSpan(HeaderBytes));
        int offset = HeaderBytes + control.Length;
        foreach (var part in parts.Values)
        {
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), part.Length);
            offset += sizeof(int);
            part.CopyTo(result.AsSpan(offset));
            offset += part.Length;
        }
        SHA256.HashData(result.AsSpan(0, offset), result.AsSpan(offset));
        return result;
    }

    internal static T Decode<T>(ReadOnlySpan<byte> bytes, int maximum = PartitionParticipantWire.MaximumRequestBytes)
    {
        if (bytes.Length > maximum)
        {
            throw new InvalidDataException("Partition frame exceeds its bound.");
        }
        // JSON remains readable for existing public/test adapters; newly emitted
        // participant requests and replies use the explicit binary frame.
        if (!bytes.StartsWith(Magic))
        {
            return JsonSerializer.Deserialize<T>(bytes, PartitionParticipantWire.Json)
            ?? throw new InvalidDataException("Missing partition message.");
        }

        if (bytes.Length < HeaderBytes + HashBytes)
        {
            throw new InvalidDataException("Truncated partition frame.");
        }

        Span<byte> hash = stackalloc byte[HashBytes];
        SHA256.HashData(bytes[..^HashBytes], hash);
        if (!CryptographicOperations.FixedTimeEquals(hash, bytes[^HashBytes..]))
        {
            throw new InvalidDataException("Partition frame checksum differs.");
        }

        int controlLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[ControlLengthOffset..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[PartCountOffset..]);
        if (controlLength <= 0 || controlLength > bytes.Length - HeaderBytes - HashBytes || count < 0 || count > MaximumParts)
        {
            throw new InvalidDataException("Invalid partition frame directory.");
        }

        int offset = HeaderBytes + controlLength;
        var parts = new Parts(maximum);
        using var scope = new Scope(parts);
        for (int index = 0; index < count; index++)
        {
            if (offset > bytes.Length - HashBytes - sizeof(int))
            {
                throw new InvalidDataException("Truncated partition part.");
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
            offset += sizeof(int);
            if (length < 0 || length > bytes.Length - HashBytes - offset)
            {
                throw new InvalidDataException("Invalid partition part length.");
            }

            parts.Add(bytes.Slice(offset, length).ToArray());
            offset += length;
        }
        if (offset != bytes.Length - HashBytes)
        {
            throw new InvalidDataException("Trailing partition frame bytes.");
        }

        var result = JsonSerializer.Deserialize<T>(bytes.Slice(HeaderBytes, controlLength), Json)
            ?? throw new InvalidDataException("Missing partition control.");
        if (parts.Read != parts.Values.Count)
        {
            throw new InvalidDataException("Unreferenced partition payload.");
        }

        return result;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(PartitionParticipantWire.Json);
        options.Converters.Insert(0, new BytesConverter());
        options.Converters.Insert(0, new ProjectionConverter());
        return options;
    }

    private sealed class Parts(int maximum)
    {
        internal readonly List<byte[]> Values = [];
        internal long Bytes;
        internal int Read;

        internal int Add(byte[] value)
        {
            if (Values.Count >= MaximumParts || value.Length > maximum - Bytes)
            {
                throw new InvalidDataException("Partition payload exceeds its bound.");
            }

            Bytes += value.Length;
            Values.Add(value);
            return Values.Count - 1;
        }
    }

    private sealed class BytesConverter : JsonConverter<byte[]>
    {
        private static Parts Parts => _parts ?? throw new InvalidOperationException("No participant encoding scope.");

        public override byte[] Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var parts = Parts;
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int index) || index != parts.Read || index >= parts.Values.Count)
            {
                throw new InvalidDataException("Partition payload reference differs.");
            }

            parts.Read++;
            return parts.Values[index];
        }

        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) => writer.WriteNumberValue(Parts.Add(value));
    }

    private sealed record Projection(bool Binary, byte[]? Bytes, JsonElement? Json);

    private sealed class ProjectionConverter : JsonConverter<PartitionProjectionValue>
    {
        public override PartitionProjectionValue Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var value = JsonSerializer.Deserialize<Projection>(ref reader, options) ?? throw new InvalidDataException("Missing projection.");
            if (value.Binary ? value.Bytes is null || value.Json is not null : value.Bytes is not null)
            {
                throw new InvalidDataException("Invalid projection framing.");
            }

            return value.Binary ? new(value.Bytes!) : new(value.Json ?? JsonSerializer.SerializeToElement<object?>(null));
        }

        public override void Write(Utf8JsonWriter writer, PartitionProjectionValue value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            bool binary = value.Native is byte[];
            writer.WriteBoolean("binary", binary);
            writer.WritePropertyName("bytes");
            if (binary)
            {
                JsonSerializer.Serialize(writer, (byte[])value.Native!, options);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WritePropertyName("json");
            if (binary)
            {
                writer.WriteNullValue();
            }
            else
            {
                value.Write(writer); // Preserve direct typed query serialization; no intermediate DOM.
            }

            writer.WriteEndObject();
        }
    }
}
