using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace EntityForge.Values;

/// <summary>Owns deterministic JSON text and structural metrics used for bounded database admission.</summary>
public sealed record CanonicalJsonValue
{
    /// <summary>Absolute UTF-8 safety limit for input and canonical output.</summary>
    public const int AbsoluteMaximumUtf8Bytes = 16 * 1024 * 1024;

    /// <summary>Absolute JSON nesting-depth limit accepted by the parser.</summary>
    public const int AbsoluteMaximumDepth = 256;

    /// <summary>Absolute maximum member/item count for an individual JSON container.</summary>
    public const int AbsoluteMaximumContainerItems = 1_048_576;

    private CanonicalJsonValue(string value, JsonValueKind rootKind, int maximumDepth,
        int maximumObjectMembers, int maximumArrayItems)
    {
        Value = value;
        RootKind = rootKind;
        Utf8ByteCount = Encoding.UTF8.GetByteCount(value);
        MaximumDepth = maximumDepth;
        MaximumObjectMembers = maximumObjectMembers;
        MaximumArrayItems = maximumArrayItems;
    }

    /// <summary>Owned canonical JSON text.</summary>
    public string Value
    {
        get;
    }

    /// <summary>JSON kind of the root value.</summary>
    public JsonValueKind RootKind
    {
        get;
    }

    /// <summary>UTF-8 size of the canonical text in bytes.</summary>
    public int Utf8ByteCount
    {
        get;
    }

    /// <summary>Maximum nesting depth observed while canonicalizing.</summary>
    public int MaximumDepth
    {
        get;
    }

    /// <summary>Largest member count of any object in this value.</summary>
    public int MaximumObjectMembers
    {
        get;
    }

    /// <summary>Largest item count of any array in this value.</summary>
    public int MaximumArrayItems
    {
        get;
    }

    /// <summary>Whether the root is an object or array, as required for database JSON bags.</summary>
    public bool IsStructured => RootKind is JsonValueKind.Object or JsonValueKind.Array;

    /// <summary>Parses JSON and canonicalizes object-member order and scalar formatting while collecting structural limits.</summary>
    /// <param name="json">Non-blank JSON text; comments, trailing commas and duplicate object properties are not accepted.</param>
    /// <returns>An owned canonical value; parsing alone does not require an object/array root.</returns>
    /// <exception cref="ArgumentException">Input is blank or exceeds the absolute byte limits.</exception>
    /// <exception cref="JsonException">JSON syntax, duplicate properties or structural constraints are invalid.</exception>
    public static CanonicalJsonValue Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        int inputBytes = Encoding.UTF8.GetByteCount(json);
        if (inputBytes > AbsoluteMaximumUtf8Bytes)
        {
            throw new ArgumentException($"JSON exceeds the absolute {AbsoluteMaximumUtf8Bytes}-byte safety limit.", nameof(json));
        }

        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = AbsoluteMaximumDepth
        });
        var metrics = new JsonStructureMetrics();
        var buffer = new ArrayBufferWriter<byte>(Math.Min(inputBytes, AbsoluteMaximumUtf8Bytes));
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            MaxDepth = AbsoluteMaximumDepth,
            SkipValidation = false
        }))
        {
            WriteCanonical(writer, document.RootElement, 1, metrics);
        }

        if (buffer.WrittenCount > AbsoluteMaximumUtf8Bytes)
        {
            throw new ArgumentException($"Canonical JSON exceeds the absolute {AbsoluteMaximumUtf8Bytes}-byte safety limit.", nameof(json));
        }

        return new(Encoding.UTF8.GetString(buffer.WrittenSpan), document.RootElement.ValueKind,
            metrics.MaximumDepth, metrics.MaximumObjectMembers, metrics.MaximumArrayItems);
    }

    /// <summary>Parses the canonical text into an independently owned JSON element.</summary>
    /// <returns>A cloned root element that remains valid after the temporary document is disposed.</returns>
    public JsonElement ToJsonElement()
    {
        using JsonDocument document = JsonDocument.Parse(Value);
        return document.RootElement.Clone();
    }

    /// <summary>Returns the canonical JSON text.</summary>
    /// <returns>The stored JSON representation.</returns>
    public override string ToString() => Value;

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, int depth,
        JsonStructureMetrics metrics)
    {
        metrics.MaximumDepth = Math.Max(metrics.MaximumDepth, depth);
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                JsonProperty[] properties = element.EnumerateObject().ToArray();
                RejectExcessiveContainer(properties.Length);
                metrics.MaximumObjectMembers = Math.Max(metrics.MaximumObjectMembers, properties.Length);
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in properties)
                {
                    if (!names.Add(property.Name))
                    {
                        throw new JsonException($"Duplicate JSON object property '{property.Name}' is not permitted.");
                    }
                }

                foreach (JsonProperty property in properties.OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, depth + 1, metrics);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                JsonElement[] items = element.EnumerateArray().ToArray();
                RejectExcessiveContainer(items.Length);
                metrics.MaximumArrayItems = Math.Max(metrics.MaximumArrayItems, items.Length);
                foreach (JsonElement item in items)
                {
                    WriteCanonical(writer, item, depth + 1, metrics);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                WriteNumber(writer, element);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException($"Unsupported JSON value kind '{element.ValueKind}'.");
        }
    }

    private static void RejectExcessiveContainer(int count)
    {
        if (count > AbsoluteMaximumContainerItems)
        {
            throw new JsonException($"A JSON container exceeds the absolute {AbsoluteMaximumContainerItems}-item safety limit.");
        }
    }

    private sealed class JsonStructureMetrics
    {
        public int MaximumDepth
        {
            get; set;
        }

        public int MaximumObjectMembers
        {
            get; set;
        }

        public int MaximumArrayItems
        {
            get; set;
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.TryGetInt64(out long integer))
        {
            writer.WriteNumberValue(integer);
            return;
        }
        if (element.TryGetDecimal(out decimal decimalValue))
        {
            string normalized = (decimalValue == decimal.Zero ? decimal.Zero : decimalValue)
                .ToString("G29", CultureInfo.InvariantCulture);
            writer.WriteRawValue(normalized, skipInputValidation: false);
            return;
        }
        throw new JsonException("JSON number is outside EntityForge's supported deterministic decimal range.");
    }
}
