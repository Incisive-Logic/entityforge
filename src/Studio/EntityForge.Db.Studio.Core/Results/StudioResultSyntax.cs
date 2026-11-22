using System.Text.Json;
using System.Text.Json.Serialization;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Client;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Studio.Results;

/// <summary>Complete JSON for the returned result/page, without the inspector's display limits.</summary>
public static class StudioResultSyntax
{
    public static string Format(NativeQueryResult result) =>
        JsonSerializer.Serialize(result.Document, StudioResultJsonContext.Default.JsonElement);

    public static string Format(EntityProjectionQueryResult result) =>
        JsonSerializer.Serialize(EntityProjectionTransport.Encode(result),
            StudioResultJsonContext.Default.EntityProjectionTransportResponse);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(EntityProjectionTransportResponse))]
internal partial class StudioResultJsonContext : JsonSerializerContext;
