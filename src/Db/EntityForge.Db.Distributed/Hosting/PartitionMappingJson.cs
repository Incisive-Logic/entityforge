using System.Text.Json;
using System.Text.Json.Serialization;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Distributed.Hosting;

// Cache interpretation of exact immutable input, never catalogue/placement validity.
// A small bounded FIFO also bounds adversarial unique-manifest retention.
internal sealed class PartitionMappingJson : JsonConverter<EntityMappingManifest>
{
    private const int MaximumEntries = 64;
    private const int MaximumKeyCharacters = 16 * 1024;
    private static readonly JsonSerializerOptions Uncached = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    private readonly object _sync = new();
    private readonly Dictionary<string, EntityMappingManifest> _values = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public override EntityMappingManifest Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        string key = document.RootElement.GetRawText();
        if (key.Length <= MaximumKeyCharacters)
        {
            lock (_sync)
            {
                if (_values.TryGetValue(key, out var retained))
                {
                    return retained;
                }
            }
        }

        RdfWireJson.UniqueMembers(document.RootElement);
        var manifest = document.RootElement.Deserialize<EntityMappingManifest>(Uncached)
            ?? throw new InvalidDataException("Missing mapping manifest.");
        if (document.RootElement.TryGetProperty("fingerprint", out var fingerprint) && fingerprint.GetString() != manifest.Fingerprint)
        {
            throw new InvalidDataException("Mapping fingerprint differs from its content.");
        }

        if (key.Length <= MaximumKeyCharacters)
        {
            lock (_sync)
            {
                if (_values.TryGetValue(key, out var retained))
                {
                    return retained;
                }

                if (_order.Count == MaximumEntries)
                {
                    _values.Remove(_order.Dequeue());
                }

                _values.Add(key, manifest);
                _order.Enqueue(key);
            }
        }

        return manifest;
    }

    public override void Write(Utf8JsonWriter writer, EntityMappingManifest value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, Uncached);
}
