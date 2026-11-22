using EntityForge.Db.Query.Adapters;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionPublicQueryProjection
{
    private const string NativeSnapshot = "snapshotVersion";
    private static readonly JsonSerializerOptions Graph = RootOptions(typeof(GraphPatternQueryResponse));
    private static readonly JsonSerializerOptions Semantic = RootOptions(typeof(PortableEntityQueryResponse));
    private static readonly JsonSerializerOptions SemanticPage = RootOptions(typeof(PortableEntityQueryResponse), page: true);

    private static JsonSerializerOptions RootOptions(Type root, bool page = false)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != root)
            {
                return;
            }

            for (int index = info.Properties.Count - 1; index >= 0; index--)
            {
                if (info.Properties[index].Name == NativeSnapshot || page && info.Properties[index].Name == "nextContinuation")
                {
                    info.Properties.RemoveAt(index);
                }
            }
        });
        var options = new JsonSerializerOptions(PartitionParticipantWire.Json) { TypeInfoResolver = resolver };
        options.MakeReadOnly();
        return options;
    }

    internal static PartitionProjectionValue WithoutNativeSnapshot(object value) => new(new Root(value));

    internal static PartitionProjectionValue EntityPage(PortableEntityQueryResponse value) => new(new Root(value, page: true));

    internal static object? Native(PartitionProjectionValue value) => value.Native is Root root ? root.Value : value.Native;

    private sealed class Root(object value, bool page = false) : PartitionProjectionValue.IWriter
    {
        internal object Value => value;

        public void Write(Utf8JsonWriter writer)
        {
            if (value is JsonElement element)
            {
                // Preserve the previous dictionary's rejection of duplicate
                // public root fields from a decoded remote result.
                var members = new HashSet<string>(StringComparer.Ordinal);
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name != NativeSnapshot)
                    {
                        if (!members.Add(property.Name))
                        {
                            throw new InvalidDataException("Duplicate public query field.");
                        }

                        property.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
                return;
            }
            var options = value switch
            {
                GraphPatternQueryResponse => Graph,
                PortableEntityQueryResponse => page ? SemanticPage : Semantic,
                _ => throw new InvalidDataException("Unsupported native public query projection.")
            };
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
        }
    }
}
