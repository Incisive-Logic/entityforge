using System.Text.Json.Serialization;

namespace EntityForge.Db.Studio.Avalonia;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class GraphQlVariablesJsonContext : JsonSerializerContext;
