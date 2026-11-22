using System.Text;

namespace EntityForge.Db.Mapping;

// Explicit internal schemas; no reflection serializer or untyped metadata fallback.
internal interface IDeliveryMetadata<TSelf> where TSelf : IDeliveryMetadata<TSelf>
{
    static abstract int TypeTag
    {
        get;
    }

    static abstract TSelf Read(BinaryReader reader);

    void Write(BinaryWriter writer);
}

internal static class DeliveryBinary
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal const int MaximumBytes = 1024 * 1024;

    internal static byte[] Encode(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        write(writer);
        writer.Flush();
        if (stream.Length > MaximumBytes)
        {
            throw new InvalidDataException("Delivery metadata exceeds its bound.");
        }

        return stream.ToArray();
    }

    internal static bool Flag(BinaryReader reader) => reader.ReadByte() switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException("Invalid metadata flag.")
    };

    internal static int Count(BinaryReader reader, int maximum = MaximumBytes)
    {
        int value = reader.ReadInt32();
        if (value < 0 || value > maximum || value > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("Invalid metadata length.");
        }

        return value;
    }

    internal static string Text(BinaryReader reader)
    {
        // BinaryWriter's UTF-8 prefix, checked against remaining bytes before allocating.
        int length = reader.Read7BitEncodedInt();
        if (length < 0 || length > MaximumBytes || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("Invalid metadata string length.");
        }

        return Utf8.GetString(reader.ReadBytes(length));
    }

    internal static void Optional(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }

    internal static string? Optional(BinaryReader reader) => Flag(reader) ? Text(reader) : null;

    internal static void Guid(BinaryWriter writer, System.Guid value) => writer.Write(value.ToByteArray());

    internal static System.Guid Guid(BinaryReader reader)
    {
        const int size = 16;
        byte[] bytes = reader.ReadBytes(size);
        if (bytes.Length != size)
        {
            throw new InvalidDataException("Truncated metadata identity.");
        }

        return new(bytes);
    }

    internal static void Bytes(BinaryWriter writer, byte[] bytes)
    {
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    internal static byte[] Bytes(BinaryReader reader) => reader.ReadBytes(Count(reader));

    internal static T EnumValue<T>(BinaryReader reader) where T : struct, Enum
    {
        int value = reader.ReadInt32();
        // Validate the integer before narrowing enum underlying types.
        if (EnumValues<T>.Values.TryGetValue(value, out T item))
        {
            return item;
        }

        throw new InvalidDataException("Unknown metadata enum value.");
    }

    private static class EnumValues<T> where T : struct, Enum
    {
        internal static readonly IReadOnlyDictionary<int, T> Values = Enum.GetValues<T>()
            .Distinct().ToDictionary(value => Convert.ToInt32(value));
    }

    internal static void Integers(BinaryWriter writer, int[]? values)
    {
        writer.Write(values is not null);
        if (values is null)
        {
            return;
        }

        writer.Write(values.Length);
        foreach (int n in values)
        {
            writer.Write(n);
        }
    }

    internal static int[]? Integers(BinaryReader reader)
    {
        if (!Flag(reader))
        {
            return null;
        }

        int count = Count(reader);
        if (count > (reader.BaseStream.Length - reader.BaseStream.Position) / sizeof(int))
        {
            throw new InvalidDataException("Invalid integer array.");
        }

        var values = new int[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = reader.ReadInt32();
        }

        return values;
    }

    internal static void Maps(BinaryWriter writer, IReadOnlyList<EntityMappingManifest> maps) => Bytes(writer, EntityMappingBinaryCodec.Encode(maps));

    internal static EntityMappingManifest[] Maps(BinaryReader reader) => EntityMappingBinaryCodec.Decode(Bytes(reader));

    internal static EntityMappingManifest Map(BinaryReader reader)
    {
        var maps = Maps(reader);
        return maps.Length == 1 ? maps[0] : throw new InvalidDataException("Expected one mapping.");
    }
}
