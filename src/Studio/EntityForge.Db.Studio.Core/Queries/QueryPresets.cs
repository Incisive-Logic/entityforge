using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EntityForge.Db.AppModel.Queries;

namespace EntityForge.Db.Studio.Queries;

public sealed record QueryPreset(string Id, string Name, string Profile, string Kind,
    string Text, string Variables, SemanticQueryDocument? Builder);

public sealed record QueryPresetDocument(int Version, IReadOnlyList<QueryPreset> Presets);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, Converters = new[] { typeof(QueryOperatorSetConverter) })]
[JsonSerializable(typeof(QueryPresetDocument))]
public sealed partial class QueryPresetJsonContext : JsonSerializerContext;

public static class QueryPresetFormat
{
    public const int MaximumBytes = 2 * 1024 * 1024;

    public static string Serialize(QueryPresetDocument document)
    {
        Validate(document);
        string json = JsonSerializer.Serialize(document, QueryPresetJsonContext.Default.QueryPresetDocument);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
        {
            throw new InvalidDataException("The preset collection exceeds 2 MiB. Export smaller collections.");
        }

        return json;
    }

    public static QueryPresetDocument Deserialize(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
        {
            throw new InvalidDataException("Preset files must be smaller than 2 MiB.");
        }

        var document = JsonSerializer.Deserialize(json.TrimStart('\uFEFF'), QueryPresetJsonContext.Default.QueryPresetDocument)
            ?? throw new InvalidDataException("The file contains no presets.");
        Validate(document);
        return document;
    }

    public static void Validate(QueryPresetDocument document)
    {
        if (document.Version != 1)
        {
            throw new InvalidDataException("This preset file version is not supported.");
        }

        if (document.Presets is null || document.Presets.Count > 100)
        {
            throw new InvalidDataException("A collection can contain up to 100 presets.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in document.Presets)
        {
            if (p is null || !Guid.TryParse(p.Id, out _) || !ids.Add(p.Id))
            {
                throw new InvalidDataException("Preset identifiers are invalid or duplicated.");
            }

            if (String.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 120)
            {
                throw new InvalidDataException("Preset names must contain 1–120 characters.");
            }

            if (p.Text is null || p.Variables is null || p.Text.Length > HttpNativeQueryExecutor.MaximumQueryCharacters || p.Variables.Length > HttpNativeQueryExecutor.MaximumQueryCharacters)
            {
                throw new InvalidDataException("A query or its variables exceed the supported text limit.");
            }

            if ((p.Profile, p.Kind) is not (("semantic", "builder") or ("semantic", "graphql") or ("property-graph", "gql") or ("rdf", "sparql")))
            {
                throw new InvalidDataException("The preset has an unsupported database profile or query type.");
            }

            using var variables = JsonDocument.Parse(p.Variables);
            if (variables.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Variables must be a JSON object.");
            }

            if (p.Kind == "builder")
            {
                var q = p.Builder ?? throw new InvalidDataException("The builder query is missing.");
                if (q.PrimaryConcept is null || String.IsNullOrWhiteSpace(q.PrimaryConcept.Identifier) || q.Conditions is null || q.Projection is null)
                {
                    throw new InvalidDataException("The builder query is incomplete.");
                }

                ValidateBlocks(q.Conditions, 0);
                if (q.Projection.Any(f => f is null || f.Property is null || f.Property.Operators is null || String.IsNullOrWhiteSpace(f.ResultName)) ||
                    (q.OrderBy ?? []).Any(o => o is null || o.Property is null || o.Property.Operators is null))
                {
                    throw new InvalidDataException("The builder projection or ordering is invalid.");
                }
            }
            else if (p.Builder is not null)
            {
                throw new InvalidDataException("A text query cannot contain a builder document.");
            }
        }
    }

    private static void ValidateBlocks(IReadOnlyList<SemanticQueryBlock> blocks, int depth)
    {
        if (depth > 16 || blocks.Count > 1000)
        {
            throw new InvalidDataException("The builder query is too complex.");
        }

        foreach (var block in blocks)
        {
            if (block is null || String.IsNullOrWhiteSpace(block.Id))
            {
                throw new InvalidDataException("A builder condition is invalid.");
            }

            switch (block)
            {
                case SemanticPropertyConditionBlock p when p.Property is not null && p.Property.Operators is not null && p.Value.ValueKind != JsonValueKind.Undefined:
                    break;
                case SemanticLogicalQueryBlock g when g.Children is not null:
                    ValidateBlocks(g.Children, depth + 1);
                    break;
                case SemanticRelationshipConditionBlock r when r.Relationship is not null && r.TargetConcept is not null && r.Conditions is not null:
                    ValidateBlocks(r.Conditions, depth + 1);
                    break;
                default:
                    throw new InvalidDataException("A builder condition is incomplete.");
            }
        }
    }
}

public interface IQueryPresetStore
{
    ValueTask<string?> ReadAsync();

    ValueTask WriteAsync(string json);
}

public sealed class MemoryQueryPresetStore : IQueryPresetStore
{
    private string? _json;

    public ValueTask<string?> ReadAsync() => ValueTask.FromResult(_json);

    public ValueTask WriteAsync(string json)
    {
        _json = json;
        return ValueTask.CompletedTask;
    }
}

public sealed class FileQueryPresetStore(string path) : IQueryPresetStore
{
    public async ValueTask<string?> ReadAsync() => File.Exists(path) ? await File.ReadAllTextAsync(path) : null;

    public async ValueTask WriteAsync(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".pending";
        await File.WriteAllTextAsync(temporary, json);
        File.Move(temporary, path, true);
    }
}

/// <summary>Serializes mutations across Query panels. Failed reads never overwrite the saved collection.</summary>
public sealed class QueryPresetLibrary(IQueryPresetStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<QueryPresetDocument> ReadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return await ReadCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<QueryPresetDocument> ReadCore() => await store.ReadAsync() is { } json
        ? QueryPresetFormat.Deserialize(json) : new(1, []);

    private async Task Change(Func<QueryPresetDocument, QueryPresetDocument> change)
    {
        await _gate.WaitAsync();
        try
        {
            await store.WriteAsync(QueryPresetFormat.Serialize(change(await ReadCore())));
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SaveAsync(QueryPreset preset, bool replace = false) => Change(d =>
    {
        if (replace && !d.Presets.Any(p => p.Id == preset.Id))
        {
            throw new InvalidOperationException("This preset was removed. Save it as a new preset.");
        }

        if (!replace && d.Presets.Any(p => p.Id == preset.Id))
        {
            throw new InvalidOperationException("This preset already exists.");
        }

        return d with
        {
            Presets = d.Presets.Where(p => p.Id != preset.Id).Append(preset with
            {
                Name = preset.Name.Trim()
            }).ToArray()
        };
    });

    public Task RenameAsync(string id, string name) => Change(d => d with { Presets = d.Presets.Select(p => p.Id == id ? p with { Name = name.Trim() } : p).ToArray() });

    public Task DeleteAsync(string id) => Change(d => d with { Presets = d.Presets.Where(p => p.Id != id).ToArray() });

    public Task ImportAsync(string json)
    {
        var imported = QueryPresetFormat.Deserialize(json);
        return Change(d =>
        {
            var presets = d.Presets.ToList();
            var names = presets.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var p in imported.Presets)
            {
                string name = p.Name;
                int suffix = 1;
                while (!names.Add(name))
                {
                    name = p.Name[..Math.Min(p.Name.Length, 100)] + $" (imported {suffix++})";
                }

                presets.Add(p with
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = name
                });
            }
            return new(1, presets);
        });
    }
}

public sealed class QueryOperatorSetConverter : JsonConverter<IReadOnlySet<QueryComparisonOperator>>
{
    public override IReadOnlySet<QueryComparisonOperator> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected query operators.");
        }

        var values = new HashSet<QueryComparisonOperator>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return values;
            }

            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int value) || !Enum.IsDefined((QueryComparisonOperator)value))
            {
                throw new JsonException("Invalid query operator.");
            }

            values.Add((QueryComparisonOperator)value);
        }
        throw new JsonException("Incomplete query operators.");
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlySet<QueryComparisonOperator> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value.OrderBy(x => x))
        {
            writer.WriteNumberValue((int)item);
        }

        writer.WriteEndArray();
    }
}
