using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntityForge.UI.Workspace;

/// <summary>Owns the stable JSON boundary for user-configurable workspace documents.</summary>
public static class WorkspaceDocumentSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly WorkspaceDocumentJsonContext JsonContext = new(Options);

    public static string Serialize(WorkspaceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, JsonContext.WorkspaceDocument);
    }

    public static WorkspaceDocument Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, JsonContext.WorkspaceDocument)
            ?? throw new JsonException("The workspace document was empty.");
    }
}

[JsonSerializable(typeof(WorkspaceDocument))]
internal sealed partial class WorkspaceDocumentJsonContext : JsonSerializerContext;
