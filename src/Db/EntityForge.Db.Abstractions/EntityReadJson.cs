using System.Text.Json;

namespace EntityForge.Db.Mapping;

// Strict framing helpers for profile decoders. Missing fields never become CLR defaults/absence.
internal static class EntityReadJson
{
    internal static JsonElement Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : throw new InvalidDataException($"Missing read-result member {name}.");

    internal static JsonElement.ArrayEnumerator Array(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray() : throw new InvalidDataException("Expected a read-result array.");

    internal static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new InvalidDataException("Expected a read-result string.");

    internal static string? OptionalText(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : Text(value);

    internal static Guid Identity(JsonElement value)
    {
        var field = Field(value, "value");
        return field.ValueKind == JsonValueKind.String && field.TryGetGuid(out var id) && id != Guid.Empty
            ? id : throw new InvalidDataException("Invalid native read identity.");
    }

    internal static void UniqueMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in value.EnumerateObject())
            {
                if (!names.Add(member.Name))
                {
                    throw new InvalidDataException("Duplicate read-result member.");
                }

                UniqueMembers(member.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
            {
                UniqueMembers(child);
            }
        }
    }
}
