using System.Text.Json;

namespace EntityForge.Db.Mapping;

// A bounded native read description, never producer code. Links name profile predicates and
// another shape in the same closure; values and object construction remain frontend concerns.
internal sealed record EntityReadLink(string Predicate, int Target);

internal sealed record EntityReadShape(string Kind, string? Label, JsonElement? Fields, EntityReadLink[] Links);

internal sealed record EntityReadRecipe(Guid ReadId, JsonElement Identity, EntityReadShape[] Shapes,
    bool IncludeContributionGuard = true)
{
    internal const int MaximumShapes = 64, MaximumObservations = 1000;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    internal void Validate(IEntityReadRecipePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!Enum.IsDefined(policy.Profile))
        {
            throw new ArgumentException("Unknown read profile.");
        }

        if (!IncludeContributionGuard && !policy.SupportsContributionGuard)
        {
            throw new ArgumentException("Only Semantic reads have a contribution-guard mode.");
        }

        if (ReadId == Guid.Empty || Shapes is not { Length: > 0 and <= MaximumShapes })
        {
            throw new ArgumentException("A bounded read shape and request identity are required.");
        }

        foreach (var shape in Shapes)
        {
            if (shape is null || shape.Links is null || shape.Links.Length > MaximumObservations ||
                shape.Links.Any(link => link is null || string.IsNullOrWhiteSpace(link.Predicate) || link.Target < -1 || link.Target >= Shapes.Length) ||
                shape.Links.Select(link => link.Predicate).Distinct(StringComparer.Ordinal).Count() != shape.Links.Length)
            {
                throw new ArgumentException("Invalid native read shape.");
            }

            policy.ValidateShape(shape);
        }
    }

    internal static string Key(string kind, JsonElement query)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(query, writer);
        }

        return kind + ":" + Convert.ToBase64String(buffer.ToArray());
        static void Write(JsonElement value, Utf8JsonWriter writer)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var properties = value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                {
                    throw new InvalidDataException("Duplicate read-query member.");
                }

                writer.WriteStartObject();
                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer);
                }
                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    Write(item, writer);
                }

                writer.WriteEndArray();
            }
            else
            {
                value.WriteTo(writer);
            }
        }
    }
}

internal sealed record EntityReadObservation(string Kind, JsonElement Query, JsonElement Value);

internal interface IEntityReadRecipeScope
{
    ValueTask PrefetchRecipeAsync(EntityReadRecipe recipe, CancellationToken token);
}

internal interface IEntityCompleteReadClient
{
    ValueTask<object> ReadCompleteAsync(IReadOnlyList<EntityMappingManifest> manifests, EntityReadRecipe recipe,
        Uri? source, Uri? partition, TimeSpan duration, CancellationToken token);
}

internal interface IEntityReadRecipeProvider
{
    ValueTask<IAsyncDisposable> OpenRecipeAsync(IReadOnlyList<EntityMappingManifest> manifests, EntityReadRecipe recipe,
        Uri? source, Uri? partition, TimeSpan duration, CancellationToken token);
}
