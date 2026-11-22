using System.Text;

namespace EntityForge.Db.Mapping;

// Value freezing only. Profile execution owns identity, interpretation and admission.
internal static class EntityValueCapture
{
    internal const int MaximumBytes = 64 * 1024 * 1024;
    internal const int MaximumDepth = 64;
    internal const int MaximumValues = 100_000;
    private const int GuidBytes = 16;

    private static ReadOnlySpan<byte> Magic => "EFVC0001"u8;

    private static readonly UTF8Encoding Utf8 = new(false, true);

    private enum Kind : byte
    {
        Null, Entity, Reference, Set, Sequence, String, Boolean, UInt8, Int8, Int16, UInt16,
        Int32, UInt32, Int64, Float32, Float64, Decimal, Guid, Uri, Bytes, Date, Time, DateTime, Instant, Duration, CalendarDate, CalendarDuration, SpatialPoint, LocalTime, LocalDateTime, ZonedTime, ZonedDateTime
    }

    internal static byte[] Encode(object? value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(Magic);
        var active = new HashSet<object>(ReferenceEqualityComparer.Instance);
        int count = 0;
        void Text(string text)
        {
            int length = Utf8.GetByteCount(text);
            if (length > MaximumBytes - stream.Length - sizeof(int))
            {
                throw new InvalidOperationException("Captured values exceed their byte bound.");
            }

            writer.Write(length);
            DatabaseUtf8Write.Write(writer, text, Utf8);
        }
        void Tag(Kind kind) => writer.Write((byte)kind);
        void Write(object? item, int depth)
        {
            if (depth > MaximumDepth || ++count > MaximumValues)
            {
                throw new InvalidOperationException("Captured values exceed their structural bounds.");
            }

            switch (item)
            {
                case null:
                    Tag(Kind.Null);
                    break;
                case Entity entity:
                    if (!active.Add(entity))
                    {
                        throw new InvalidOperationException("Captured owned values cannot be cyclic.");
                    }

                    Tag(Kind.Entity);
                    writer.Write(entity.Members.Count);
                    foreach (var member in entity.Members.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        Text(member.Key);
                        Write(member.Value, depth + 1);
                    }
                    active.Remove(entity);
                    break;
                case EntityReference reference:
                    Tag(Kind.Reference);
                    Write(reference.Identity, depth + 1);
                    break;
                case EntityCollection collection:
                    if (!active.Add(collection))
                    {
                        throw new InvalidOperationException("Captured collections cannot be cyclic.");
                    }

                    Tag(collection.Kind == EntityCollectionKind.Set ? Kind.Set : Kind.Sequence);
                    writer.Write(collection.Items.Count);
                    foreach (var element in collection.Items)
                    {
                        Write(element, depth + 1);
                    }

                    active.Remove(collection);
                    break;
                case string x:
                    Tag(Kind.String);
                    Text(x);
                    break;
                case bool x:
                    Tag(Kind.Boolean);
                    writer.Write(x);
                    break;
                case byte x:
                    Tag(Kind.UInt8);
                    writer.Write(x);
                    break;
                case sbyte x:
                    Tag(Kind.Int8);
                    writer.Write(x);
                    break;
                case short x:
                    Tag(Kind.Int16);
                    writer.Write(x);
                    break;
                case ushort x:
                    Tag(Kind.UInt16);
                    writer.Write(x);
                    break;
                case int x:
                    Tag(Kind.Int32);
                    writer.Write(x);
                    break;
                case uint x:
                    Tag(Kind.UInt32);
                    writer.Write(x);
                    break;
                case long x:
                    Tag(Kind.Int64);
                    writer.Write(x);
                    break;
                case float x:
                    Tag(Kind.Float32);
                    writer.Write(x);
                    break;
                case double x:
                    Tag(Kind.Float64);
                    writer.Write(x);
                    break;
                case decimal x:
                    Tag(Kind.Decimal);
                    writer.Write(x);
                    break;
                case Guid x:
                    Tag(Kind.Guid);
                    writer.Write(x.ToByteArray());
                    break;
                case Uri x when x.IsAbsoluteUri:
                    Tag(Kind.Uri);
                    Text(x.AbsoluteUri);
                    break;
                case byte[] x:
                    if (x.Length > MaximumBytes - stream.Length - sizeof(int) - sizeof(byte))
                    {
                        throw new InvalidOperationException("Captured values exceed their byte bound.");
                    }

                    Tag(Kind.Bytes);
                    writer.Write(x.Length);
                    writer.Write(x);
                    break;
                case DateOnly x:
                    Tag(Kind.Date);
                    writer.Write(x.DayNumber);
                    break;
                case EntityForge.Values.CalendarDate x:
                    Tag(Kind.CalendarDate);
                    writer.Write(x.EpochDay);
                    break;
                case EntityForge.Values.CalendarDuration x:
                    Tag(Kind.CalendarDuration);
                    writer.Write(x.Months);
                    writer.Write(x.Days);
                    writer.Write(x.Seconds);
                    writer.Write(x.Nanoseconds);
                    break;
                case EntityForge.Values.SpatialPoint x:
                    Tag(Kind.SpatialPoint);
                    writer.Write(x.Srid);
                    writer.Write(x.X);
                    writer.Write(x.Y);
                    if (x.Z is { } z)
                    {
                        writer.Write(z);
                    }
                    break;
                case EntityForge.Values.ZonedDateTime x:
                    Tag(Kind.ZonedDateTime);
                    writer.Write(x.DateTime.Date.EpochDay);
                    writer.Write(x.DateTime.Time.NanosecondsOfDay);
                    writer.Write(x.OffsetSeconds);
                    Text(x.ZoneId ?? String.Empty);
                    break;
                case EntityForge.Values.ZonedTime x:
                    Tag(Kind.ZonedTime);
                    writer.Write(x.Time.NanosecondsOfDay);
                    writer.Write(x.OffsetSeconds);
                    break;
                case EntityForge.Values.LocalTime x:
                    Tag(Kind.LocalTime);
                    writer.Write(x.NanosecondsOfDay);
                    break;
                case EntityForge.Values.LocalDateTime x:
                    Tag(Kind.LocalDateTime);
                    writer.Write(x.Date.EpochDay);
                    writer.Write(x.Time.NanosecondsOfDay);
                    break;
                case TimeOnly x:
                    Tag(Kind.Time);
                    writer.Write(x.Ticks);
                    break;
                case DateTime x:
                    Tag(Kind.DateTime);
                    writer.Write(x.Ticks);
                    writer.Write((byte)x.Kind);
                    break;
                case DateTimeOffset x:
                    Tag(Kind.Instant);
                    writer.Write(x.Ticks);
                    writer.Write(checked((short)x.Offset.TotalMinutes));
                    break;
                case TimeSpan x:
                    Tag(Kind.Duration);
                    writer.Write(x.Ticks);
                    break;
                default:
                    throw new NotSupportedException($"Convert '{item.GetType().Name}' to a supported mapped value before capture.");
            }
            if (stream.Length > MaximumBytes)
            {
                throw new InvalidOperationException("Captured values exceed their byte bound.");
            }
        }
        Write(value, 0);
        writer.Flush();
        return stream.ToArray();
    }

    internal static object? Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumBytes || !bytes.Span.StartsWith(Magic))
        {
            throw new InvalidDataException("Invalid captured value framing.");
        }

        using var stream = new MemoryStream(bytes.ToArray(), 0, bytes.Length, false, true);
        using var reader = new BinaryReader(stream, Utf8);
        stream.Position = Magic.Length;
        int count = 0;
        int Length(int maximum)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > maximum || length > stream.Length - stream.Position)
            {
                throw new InvalidDataException("Invalid captured value length.");
            }

            return length;
        }
        byte[] Exact(int length)
        {
            if (length > stream.Length - stream.Position)
            {
                throw new InvalidDataException("Truncated captured value.");
            }

            return reader.ReadBytes(length);
        }
        string Text() => DatabaseUtf8Read.Read(reader, Length(MaximumBytes), Utf8);
        object? Read(int depth)
        {
            if (depth > MaximumDepth || ++count > MaximumValues)
            {
                throw new InvalidDataException("Captured values exceed their structural bounds.");
            }

            var kind = (Kind)reader.ReadByte();
            switch (kind)
            {
                case Kind.Null:
                    return null;
                case Kind.Entity:
                    int members = Length(MaximumValues - count);
                    var result = new Dictionary<string, object?>(members, StringComparer.Ordinal);
                    string? prior = null;
                    for (int i = 0; i < members; i++)
                    {
                        string name = Text();
                        if (prior is not null && StringComparer.Ordinal.Compare(prior, name) >= 0)
                        {
                            throw new InvalidDataException("Captured members are not uniquely ordered.");
                        }

                        prior = name;
                        result.Add(name, Read(depth + 1));
                    }
                    return new Entity(result);
                case Kind.Reference:
                    return new EntityReference(Read(depth + 1) ?? throw new InvalidDataException("A reference requires an identity."));
                case Kind.Set or Kind.Sequence:
                    var items = new object?[Length(MaximumValues - count)];
                    for (int i = 0; i < items.Length; i++)
                    {
                        items[i] = Read(depth + 1);
                    }

                    return new EntityCollection(kind == Kind.Set ? EntityCollectionKind.Set : EntityCollectionKind.OrderedSequence, items);
                case Kind.String:
                    return Text();
                case Kind.Boolean:
                    return reader.ReadByte() switch
                    {
                        0 => false,
                        1 => true,
                        _ => throw new InvalidDataException("Invalid captured boolean.")
                    };
                case Kind.UInt8:
                    return reader.ReadByte();
                case Kind.Int8:
                    return reader.ReadSByte();
                case Kind.Int16:
                    return reader.ReadInt16();
                case Kind.UInt16:
                    return reader.ReadUInt16();
                case Kind.Int32:
                    return reader.ReadInt32();
                case Kind.UInt32:
                    return reader.ReadUInt32();
                case Kind.Int64:
                    return reader.ReadInt64();
                case Kind.Float32:
                    return reader.ReadSingle();
                case Kind.Float64:
                    return reader.ReadDouble();
                case Kind.Decimal:
                    return reader.ReadDecimal();
                case Kind.Guid:
                    return new Guid(Exact(GuidBytes));
                case Kind.Uri:
                    string text = Text();
                    var uri = new Uri(text, UriKind.Absolute);
                    if (uri.AbsoluteUri != text)
                    {
                        throw new InvalidDataException("Captured URI is not canonical.");
                    }

                    return uri;
                case Kind.Bytes:
                    return Exact(Length(MaximumBytes));
                case Kind.Date:
                    return DateOnly.FromDayNumber(reader.ReadInt32());
                case Kind.CalendarDate:
                    return EntityForge.Values.CalendarDate.FromEpochDay(reader.ReadInt64());
                case Kind.CalendarDuration:
                    return new EntityForge.Values.CalendarDuration(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt32());
                case Kind.SpatialPoint:
                    int srid = reader.ReadInt32();
                    double x = reader.ReadDouble(), y = reader.ReadDouble();
                    return new EntityForge.Values.SpatialPoint(srid, x, y, srid is 9157 or 4979 ? reader.ReadDouble() : null);
                case Kind.ZonedDateTime:
                    return new EntityForge.Values.ZonedDateTime(new(EntityForge.Values.CalendarDate.FromEpochDay(reader.ReadInt64()), new(reader.ReadInt64())), reader.ReadInt32(), Text());
                case Kind.ZonedTime:
                    return new EntityForge.Values.ZonedTime(new(reader.ReadInt64()), reader.ReadInt32());
                case Kind.LocalTime:
                    return new EntityForge.Values.LocalTime(reader.ReadInt64());
                case Kind.LocalDateTime:
                    return new EntityForge.Values.LocalDateTime(EntityForge.Values.CalendarDate.FromEpochDay(reader.ReadInt64()), new(reader.ReadInt64()));
                case Kind.Time:
                    return new TimeOnly(reader.ReadInt64());
                case Kind.DateTime:
                    return new DateTime(reader.ReadInt64(), (DateTimeKind)reader.ReadByte());
                case Kind.Instant:
                    return new DateTimeOffset(reader.ReadInt64(), TimeSpan.FromMinutes(reader.ReadInt16()));
                case Kind.Duration:
                    return TimeSpan.FromTicks(reader.ReadInt64());
                default:
                    throw new InvalidDataException("Unsupported captured value type.");
            }
        }
        try
        {
            var result = Read(0);
            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("Trailing captured value bytes.");
            }

            return result;
        }
        catch (Exception failure) when (failure is EndOfStreamException or ArgumentException or FormatException)
        {
            throw new InvalidDataException("Invalid captured value.", failure);
        }
    }
}
