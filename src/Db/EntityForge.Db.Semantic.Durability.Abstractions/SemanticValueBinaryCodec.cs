using System.Text;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

// Native values, including values embedded in identities and durable predicates.
// JSON bags remain user data; no object/metadata JSON serialization is involved.
internal static class SemanticValueBinaryCodec
{
    internal const int MaximumValueBytes = 64 * 1024 * 1024;
    private const int GuidBytes = 16;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static void Write(BinaryWriter writer, SemanticValue value)
    {
        writer.Write((byte)value.Kind);
        switch (value)
        {
            case NullSemanticValue:
                break;
            case BooleanSemanticValue item:
                writer.Write(item.Value);
                break;
            case IntegerSemanticValue item:
                writer.Write(item.Value);
                break;
            case FloatingPointSemanticValue item:
                writer.Write(item.Value);
                break;
            case DecimalSemanticValue item:
                writer.Write(item.Value);
                break;
            case StringSemanticValue item:
                Text(writer, item.Value);
                break;
            case LanguageStringSemanticValue item:
                Text(writer, item.Value);
                Text(writer, item.Language);
                break;
            case DateSemanticValue item:
                writer.Write(item.Value.DayNumber);
                break;
            case TimeSemanticValue item:
                writer.Write(item.Value.Ticks);
                break;
            case DateTimeSemanticValue item:
                writer.Write(item.Value.Ticks);
                writer.Write(checked((short)item.Value.Offset.TotalMinutes));
                break;
            case DurationSemanticValue item:
                writer.Write(item.Value.Ticks);
                break;
            case BinarySemanticValue item:
                writer.Write(item.Value.Length);
                writer.Write(item.Value.Span);
                break;
            case AbsoluteUriSemanticValue item:
                Text(writer, item.Value.OriginalString);
                break;
            case GuidSemanticValue item:
                writer.Write(item.Value.ToByteArray());
                break;
            case JsonSemanticValue item:
                Text(writer, item.Utf8Json);
                break;
            case TypedLiteralSemanticValue item:
                Text(writer, item.LexicalValue);
                Text(writer, item.Datatype.OriginalString);
                break;
            default:
                throw new InvalidDataException("Unknown Semantic value type.");
        }
    }

    internal static SemanticValue Read(BinaryReader reader) => (SemanticValueKind)reader.ReadByte() switch
    {
        SemanticValueKind.Null => new NullSemanticValue(),
        SemanticValueKind.Boolean => new BooleanSemanticValue(Flag(reader)),
        SemanticValueKind.Integer => new IntegerSemanticValue(reader.ReadInt64()),
        SemanticValueKind.FloatingPoint => new FloatingPointSemanticValue(reader.ReadDouble()),
        SemanticValueKind.Decimal => new DecimalSemanticValue(reader.ReadDecimal()),
        SemanticValueKind.String => new StringSemanticValue(Text(reader)),
        SemanticValueKind.LanguageString => new LanguageStringSemanticValue(Text(reader), Text(reader)),
        SemanticValueKind.Date => new DateSemanticValue(DateOnly.FromDayNumber(reader.ReadInt32())),
        SemanticValueKind.Time => new TimeSemanticValue(new TimeOnly(reader.ReadInt64())),
        SemanticValueKind.DateTime => new DateTimeSemanticValue(new DateTimeOffset(reader.ReadInt64(), TimeSpan.FromMinutes(reader.ReadInt16()))),
        SemanticValueKind.Duration => new DurationSemanticValue(TimeSpan.FromTicks(reader.ReadInt64())),
        SemanticValueKind.Binary => new BinarySemanticValue(Exact(reader, reader.ReadInt32())),
        SemanticValueKind.AbsoluteUri => new AbsoluteUriSemanticValue(DatabaseDecodedIdentifiers.AbsoluteUri(Text(reader))),
        SemanticValueKind.Guid => new GuidSemanticValue(new Guid(Exact(reader, GuidBytes))),
        SemanticValueKind.Json => new JsonSemanticValue(Text(reader)),
        SemanticValueKind.TypedLiteral => new TypedLiteralSemanticValue(Text(reader), DatabaseDecodedIdentifiers.AbsoluteUri(Text(reader))),
        _ => throw new InvalidDataException("Unknown native Semantic value kind.")
    };

    internal static bool Flag(BinaryReader reader) => reader.ReadByte() switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException("Invalid native Semantic boolean.")
    };

    internal static void Text(BinaryWriter writer, string value)
    {
        int length = Utf8.GetByteCount(value);
        if (length > MaximumValueBytes)
        {
            throw new InvalidDataException("Semantic text exceeds its bound.");
        }

        writer.Write(length);
        DatabaseUtf8Write.Write(writer, value, Utf8);
    }

    internal static string Text(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        RequireLength(reader, length);
        return DatabaseUtf8Read.Read(reader, length, Utf8);
    }

    private static byte[] Exact(BinaryReader reader, int length)
    {
        RequireLength(reader, length);
        return reader.ReadBytes(length);
    }

    private static void RequireLength(BinaryReader reader, int length)
    {
        if (length < 0 || length > MaximumValueBytes || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("Invalid native Semantic value length.");
        }
    }

    internal static void Term(BinaryWriter writer, SemanticTerm value)
    {
        Text(writer, value.Identifier.OriginalString);
        writer.Write((byte)value.Role);
    }

    internal static SemanticTerm Term(BinaryReader reader)
    {
        var uri = DatabaseDecodedIdentifiers.AbsoluteUri(Text(reader));
        var role = (SemanticTermRole)reader.ReadByte();
        if (!Enum.IsDefined(role))
        {
            throw new InvalidDataException("Invalid Semantic term role.");
        }

        return new(uri, role);
    }

    internal static void Identity(BinaryWriter writer, SemanticEntityIdentity value)
    {
        Term(writer, value.Concept);
        Text(writer, value.Scheme.Identifier.OriginalString);
        Text(writer, value.Scheme.Name);
        writer.Write((byte)value.Scheme.ValueKind);
        writer.Write(value.Scheme.Description is not null);
        if (value.Scheme.Description is not null)
        {
            Text(writer, value.Scheme.Description);
        }

        Write(writer, value.Value);
    }

    internal static SemanticEntityIdentity Identity(BinaryReader reader)
    {
        var concept = Term(reader);
        var uri = DatabaseDecodedIdentifiers.AbsoluteUri(Text(reader));
        string name = DatabaseDecodedIdentifiers.Text(Text(reader));
        var kind = (SemanticValueKind)reader.ReadByte();
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidDataException("Invalid Semantic identity kind.");
        }

        string? description = Flag(reader) ? Text(reader) : null;
        return new(concept, new(uri, name, kind, description), Read(reader));
    }
}
