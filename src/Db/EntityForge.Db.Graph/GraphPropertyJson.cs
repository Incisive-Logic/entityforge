using System.Text.Json;

namespace EntityForge.Db.Graph;

internal static class GraphPropertyJson
{
    internal static DatabaseValue Map(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => new DatabaseValue.String(value.GetString()!),
        JsonValueKind.Number when value.TryGetInt64(out long integer) => new DatabaseValue.Int64(integer),
        JsonValueKind.Number => new DatabaseValue.Double(value.GetDouble()),
        JsonValueKind.True => new DatabaseValue.Boolean(true),
        JsonValueKind.False => new DatabaseValue.Boolean(false),
        JsonValueKind.Object or JsonValueKind.Array => MapJson(value),
        JsonValueKind.Null => throw new ArgumentException("Graph property values cannot be null."),
        _ => throw new ArgumentException($"Unsupported graph property JSON kind '{value.ValueKind}'.")
    };

    private static DatabaseValue MapJson(JsonElement value)
    {
        try
        {
            return new DatabaseValue.Json(value.GetRawText());
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Graph JSON property value is invalid.", nameof(value), exception);
        }
    }
}
