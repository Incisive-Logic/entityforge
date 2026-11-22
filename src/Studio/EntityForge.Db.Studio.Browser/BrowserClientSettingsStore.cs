using System.Text.Json;
using EntityForge.Db.Studio.Settings;

namespace EntityForge.Db.Studio.Browser;

internal sealed class BrowserClientSettingsStore : IStudioClientSettingsStore
{
    private const string Key = "entityforge.studio.settings";

    public ValueTask<StudioClientSettings> LoadAsync()
    {
        string? value = BrowserPreferences.Get(Key);
        return ValueTask.FromResult(value is null ? new StudioClientSettings()
            : JsonSerializer.Deserialize(value, StudioClientSettingsJsonContext.Default.StudioClientSettings) ?? new());
    }

    public ValueTask SaveAsync(StudioClientSettings settings)
    {
        BrowserPreferences.Set(Key, JsonSerializer.Serialize(settings, StudioClientSettingsJsonContext.Default.StudioClientSettings));
        return ValueTask.CompletedTask;
    }
}
