using System.Text.Json;
using System.Text.Json.Serialization;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.AppModel.Avalonia.Results;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(EntityProjectionTransportResponse))]
internal sealed partial class ProjectionDiagnosticsJsonContext : JsonSerializerContext;
