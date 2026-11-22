using System.Text.Json;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

// Shared by application and participant transports. Decode before constructing native values.
internal static class GraphWireJson
{
    internal static void UniqueMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in value.EnumerateObject())
            {
                if (!names.Add(member.Name))
                {
                    throw new InvalidDataException("Duplicate Graph wire member.");
                }

                UniqueMembers(member.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                UniqueMembers(item);
            }
        }
    }

    internal static JsonElement Field(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
            ? field : throw new InvalidDataException($"Missing Graph wire field '{name}'.");

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new InvalidDataException("Expected Graph string.");

    internal static GraphEdgeReference EdgeReference(JsonElement value)
    {
        UniqueMembers(value);
        return ReadEdgeReference(value);
    }

    private static GraphEdgeReference ReadEdgeReference(JsonElement value)
    {
        Guid Identity(string name)
        {
            var scalar = Field(Field(value, name), "value");
            if (scalar.ValueKind != JsonValueKind.String || !scalar.TryGetGuid(out var id) || id == Guid.Empty)
            {
                throw new InvalidDataException("Missing or invalid Graph edge-reference identity.");
            }

            return id;
        }
        string label = Text(Field(value, "label"));
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new InvalidDataException("Missing Graph edge-reference label.");
        }

        return new(new(Identity("id")), new(Identity("from")), new(Identity("to")), label);
    }

    internal static GraphEdge Edge(JsonElement value, GraphEdgeId expected)
    {
        UniqueMembers(value);
        var reference = ReadEdgeReference(value);
        if (reference.Id != expected)
        {
            throw new InvalidDataException("Edge reply differs from its requested identity.");
        }

        return new(reference.Id, reference.From, reference.To, reference.Label, ReadProperties(Field(value, "properties")));
    }

    internal static GraphVertex Vertex(JsonElement value, GraphVertexId expected)
    {
        UniqueMembers(value);
        var id = Field(Field(value, "id"), "value").GetGuid();
        if (id != expected.Value)
        {
            throw new InvalidDataException("Vertex reply differs from its requested identity.");
        }

        var labels = Field(value, "labels").EnumerateArray().Select(Text).ToArray();
        if (labels.Distinct(StringComparer.Ordinal).Count() != labels.Length)
        {
            throw new InvalidDataException("Duplicate vertex label.");
        }

        return new(expected, labels.ToHashSet(StringComparer.Ordinal), ReadProperties(Field(value, "properties")));
    }

    internal static IReadOnlyDictionary<string, DatabaseValue> Properties(JsonElement value)
    {
        UniqueMembers(value);
        return ReadProperties(value);
    }

    // The containing public decoder has validated the entire subtree once.
    // These private readers never accept independently supplied JSON.
    private static IReadOnlyDictionary<string, DatabaseValue> ReadProperties(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Expected Graph properties.");
        }

        return value.EnumerateObject().ToDictionary(item => item.Name, item => ReadValue(item.Value), StringComparer.Ordinal);
    }

    internal static DatabaseValue Value(JsonElement item)
    {
        UniqueMembers(item);
        return ReadValue(item);
    }

    private static DatabaseValue ReadValue(JsonElement item)
    {
        DatabaseValue value = Text(Field(item, "$value")) switch
        {
            "string" => new DatabaseValue.String(Text(Field(item, "value"))),
            "int64" => new DatabaseValue.Int64(Field(item, "value").GetInt64()),
            "double" => new DatabaseValue.Double(Double(Field(item, "value"))),
            "boolean" => new DatabaseValue.Boolean(Field(item, "value").GetBoolean()),
            "dateTimeOffset" => new DatabaseValue.DateTimeOffset(Field(item, "value").GetDateTimeOffset()),
            "localTime" => new DatabaseValue.LocalTime(new(Field(Field(item, "value"), "nanosecondsOfDay").GetInt64())),
            "uuid" => new DatabaseValue.Uuid(Field(item, "value").GetGuid()),
            "decimal" => new DatabaseValue.Decimal(Field(item, "value").GetDecimal()),
            "zonedDateTime" => new DatabaseValue.ZonedDateTime(new(ReadLocalDateTime(Field(Field(item, "value"), "dateTime")).Value, Field(Field(item, "value"), "offsetSeconds").GetInt32(), OptionalZone(Field(item, "value")))),
            "localDateTime" => ReadLocalDateTime(Field(item, "value")),
            "zonedTime" => new DatabaseValue.ZonedTime(new(new(Field(Field(Field(item, "value"), "time"), "nanosecondsOfDay").GetInt64()), Field(Field(item, "value"), "offsetSeconds").GetInt32())),
            "point" => new DatabaseValue.Point(new EntityForge.Values.SpatialPoint(
                Field(Field(item, "value"), "srid").GetInt32(), Field(Field(item, "value"), "x").GetDouble(),
                Field(Field(item, "value"), "y").GetDouble(), Field(Field(item, "value"), "z") is { ValueKind: JsonValueKind.Number } z ? z.GetDouble() : null)),
            "duration" => new DatabaseValue.Duration(new EntityForge.Values.CalendarDuration(
                Field(Field(item, "value"), "months").GetInt64(), Field(Field(item, "value"), "days").GetInt64(),
                Field(Field(item, "value"), "seconds").GetInt64(), Field(Field(item, "value"), "nanoseconds").GetInt32()),
                item.TryGetProperty("qualifier", out var qualifier) ? (DatabaseDurationQualifier)qualifier.GetByte() : DatabaseDurationQualifier.Unqualified),
            "date" => new DatabaseValue.Date(new EntityForge.Values.CalendarDate(
                Field(Field(item, "value"), "year").GetInt32(), Field(Field(item, "value"), "month").GetInt32(), Field(Field(item, "value"), "day").GetInt32())),
            "uri" => new DatabaseValue.Uri(new Uri(Text(Field(item, "value")), UriKind.Absolute)),
            "binary" => new DatabaseValue.Binary(Field(item, "value").GetBytesFromBase64()),
            "json" => new DatabaseValue.Json(Text(Field(item, "utf8Json"))),
            "array" => ReadArray(Field(item, "value")),
            _ => throw new InvalidDataException("Unknown Graph value discriminator.")
        };
        if (Field(item, "kind").GetByte() != (byte)value.Kind)
        {
            throw new InvalidDataException("Contradictory Graph value kind.");
        }

        return value;
    }

    private static DatabaseValue.Array ReadArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 10_000)
        {
            throw new InvalidDataException("Invalid stored array size or shape.");
        }
        var elements = value.EnumerateArray().Select(element =>
        {
            if (Text(Field(element, "$value")) is not ("string" or "int64" or "double" or "boolean" or "date" or "duration" or "point" or "localTime" or "localDateTime" or "zonedTime" or "zonedDateTime" or "uuid" or "decimal"))
            {
                throw new InvalidDataException("Invalid stored array element.");
            }
            return ReadValue(element);
        }).ToArray();
        return new(elements);
    }

    private static string? OptionalZone(JsonElement value) => value.TryGetProperty("zoneId", out var zone) && zone.ValueKind != JsonValueKind.Null ? Text(zone) : null;

    private static DatabaseValue.LocalDateTime ReadLocalDateTime(JsonElement value)
    {
        var date = Field(value, "date");
        return new(new(new(Field(date, "year").GetInt32(), Field(date, "month").GetInt32(), Field(date, "day").GetInt32()),
            new(Field(Field(value, "time"), "nanosecondsOfDay").GetInt64())));
    }

    private static double Double(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() switch
            {
                "NaN" => double.NaN,
                "Infinity" => double.PositiveInfinity,
                "-Infinity" => double.NegativeInfinity,
                _ => throw new InvalidDataException("Unknown named Graph double.")
            };
        }

        throw new InvalidDataException("Invalid or overflowing Graph double.");
    }
}
