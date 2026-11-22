using System.Text.Json;
using System.Text.Json.Serialization;
using EntityForge.UI;

namespace EntityForge.Db.Studio.Settings;

/// <summary>Preferences belonging to this Studio client, independently of its database connection.</summary>
public sealed record StudioClientSettings(EntityForgeTheme Theme = EntityForgeTheme.Dark);

[JsonSerializable(typeof(StudioClientSettings))]
public partial class StudioClientSettingsJsonContext : JsonSerializerContext;

public interface IStudioClientSettingsStore
{
    ValueTask<StudioClientSettings> LoadAsync();

    ValueTask SaveAsync(StudioClientSettings settings);
}

public sealed class MemoryStudioClientSettingsStore : IStudioClientSettingsStore
{
    private StudioClientSettings _settings = new();

    public ValueTask<StudioClientSettings> LoadAsync() => ValueTask.FromResult(_settings);

    public ValueTask SaveAsync(StudioClientSettings settings)
    {
        _settings = settings;
        return ValueTask.CompletedTask;
    }
}

public sealed class JsonStudioClientSettingsStore(string path) : IStudioClientSettingsStore
{
    public async ValueTask<StudioClientSettings> LoadAsync() => File.Exists(path)
        ? JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), StudioClientSettingsJsonContext.Default.StudioClientSettings) ?? new()
        : new();

    public async ValueTask SaveAsync(StudioClientSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string pending = path + ".pending";
        await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(settings, StudioClientSettingsJsonContext.Default.StudioClientSettings));
        File.Move(pending, path, overwrite: true);
    }
}
