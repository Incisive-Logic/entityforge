namespace EntityForge.Query.Values;

// Internal, non-durable transport for captured scalar expressions. Graph elements
// keep their profile-specific representation instead of losing identity/evidence.
internal static class QueryScalarBinaryCodec
{
    internal static void Write(BinaryWriter writer, QueryValue value, int depth = 0)
    {
        RequireDepth(depth);
        switch (value)
        {
            case UnboundValue:
                writer.Write((byte)0);
                break;
            case NullValue:
                writer.Write((byte)1);
                break;
            case LiteralValue literal:
                writer.Write((byte)2);
                writer.Write(literal.LexicalValue);
                writer.Write((int)literal.ScalarKind);
                Optional(writer, literal.DatatypeIdentifier?.OriginalString);
                Optional(writer, literal.Language);
                writer.Write(literal.Direction is { } direction ? (int)direction : -1);
                break;
            case IdentifierValue identifier:
                writer.Write((byte)3);
                writer.Write(identifier.Identifier);
                break;
            case JsonValue json:
                writer.Write((byte)4);
                writer.Write(json.Value.ToString());
                break;
            case ListValue list:
                writer.Write((byte)5);
                WriteCount(writer, list.Items.Count);
                foreach (var item in list.Items)
                {
                    Write(writer, item, depth + 1);
                }
                break;
            case MapValue map:
                writer.Write((byte)6);
                WriteCount(writer, map.Values.Count);
                foreach (var pair in map.Values)
                {
                    writer.Write(pair.Key);
                    Write(writer, pair.Value, depth + 1);
                }
                break;
            default:
                throw new NotSupportedException("Captured scalar codec does not serialize graph elements or paths.");
        }
    }

    internal static QueryValue Read(BinaryReader reader, int depth = 0)
    {
        RequireDepth(depth);
        switch (reader.ReadByte())
        {
            case 0:
                return UnboundValue.Instance;
            case 1:
                return NullValue.Instance;
            case 2:
                string lexical = reader.ReadString();
                var kind = (QueryScalarKind)reader.ReadInt32();
                string? datatype = Optional(reader);
                string? language = Optional(reader);
                int direction = reader.ReadInt32();
                if (!Enum.IsDefined(kind) || direction != -1 && !Enum.IsDefined((TextDirection)direction))
                {
                    throw new InvalidDataException("Invalid captured scalar metadata.");
                }
                return new LiteralValue(lexical, kind, datatype is null ? null : new Uri(datatype, UriKind.Absolute), language,
                    direction == -1 ? null : (TextDirection)direction);
            case 3:
                return new IdentifierValue(reader.ReadString());
            case 4:
                return new JsonValue(reader.ReadString());
            case 5:
                int count = ReadCount(reader);
                var items = new QueryValue[count];
                for (int index = 0; index < count; index++)
                {
                    items[index] = Read(reader, depth + 1);
                }
                return new ListValue(items);
            case 6:
                int entries = ReadCount(reader);
                var values = new Dictionary<string, QueryValue>(StringComparer.Ordinal);
                for (int index = 0; index < entries; index++)
                {
                    values.Add(reader.ReadString(), Read(reader, depth + 1));
                }
                return new MapValue(values);
            default:
                throw new InvalidDataException("Invalid captured scalar tag.");
        }
    }

    private static void RequireDepth(int depth)
    {
        if (depth > 32)
        {
            throw new InvalidDataException("Captured scalar nesting exceeds 32 levels.");
        }
    }

    private static void WriteCount(BinaryWriter writer, int count)
    {
        if (count > 10000)
        {
            throw new InvalidDataException("Captured scalar collection exceeds 10000 items.");
        }
        writer.Write(count);
    }

    private static int ReadCount(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count is < 0 or > 10000)
        {
            throw new InvalidDataException("Invalid captured scalar collection count.");
        }
        return count;
    }

    private static void Optional(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }

    private static string? Optional(BinaryReader reader) => reader.ReadByte() switch
    {
        0 => null,
        1 => reader.ReadString(),
        _ => throw new InvalidDataException("Invalid captured scalar presence flag.")
    };
}
