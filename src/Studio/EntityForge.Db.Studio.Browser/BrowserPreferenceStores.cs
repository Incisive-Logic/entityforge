using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using EntityForge.Db.AppModel.Connections;
using EntityForge.Db.Studio.Workspace;
using EntityForge.UI.Workspace;

namespace EntityForge.Db.Studio.Browser;

internal static partial class BrowserPreferences
{
    [JSImport("getPreference", "studioHost")]
    internal static partial string? Get(string key);

    [JSImport("setPreference", "studioHost")]
    internal static partial void Set(string key, string value);
}

internal sealed class BrowserWorkspaceStore : IStudioWorkspaceStore
{
    // Preserve existing browser preferences across the DB Studio rename.
    private const string Key = "entityforge.studio.workspace";

    public ValueTask<WorkspaceDocument?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? value = BrowserPreferences.Get(Key);
        return ValueTask.FromResult(value is null ? null : WorkspaceDocumentSerializer.Deserialize(value));
    }

    public ValueTask SaveAsync(WorkspaceDocument document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BrowserPreferences.Set(Key, WorkspaceDocumentSerializer.Serialize(document));
        return ValueTask.CompletedTask;
    }
}

internal sealed class BrowserConnectionProfileStore : IEntityForgeConnectionProfileStore
{
    // This persisted key is intentionally independent of the assembly name.
    private const string Key = "entityforge.studio.connections";

    public ValueTask<IReadOnlyList<EntityForgeConnectionProfile>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? value = BrowserPreferences.Get(Key);
        IReadOnlyList<EntityForgeConnectionProfile> profiles = value is null
            ? []
            : (JsonSerializer.Deserialize(value, BrowserJsonContext.Default.EntityForgeConnectionProfileArray) ?? [])
                .Select(profile => profile.Normalize()).ToArray();
        return ValueTask.FromResult(profiles);
    }

    public ValueTask SaveAsync(
        IReadOnlyList<EntityForgeConnectionProfile> profiles,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EntityForgeConnectionProfile[] normalized = profiles.Select(profile => profile.Normalize()).ToArray();
        BrowserPreferences.Set(Key, JsonSerializer.Serialize(
            normalized, BrowserJsonContext.Default.EntityForgeConnectionProfileArray));
        return ValueTask.CompletedTask;
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(EntityForgeConnectionProfile[]))]
internal sealed partial class BrowserJsonContext : JsonSerializerContext;

internal sealed class BrowserQueryPresetStore : EntityForge.Db.Studio.Queries.IQueryPresetStore
{
    private const string Key = "entityforge.studio.query-presets";

    public ValueTask<string?> ReadAsync() => ValueTask.FromResult(BrowserPreferences.Get(Key));

    public ValueTask WriteAsync(string json)
    {
        BrowserPreferences.Set(Key, json);
        return ValueTask.CompletedTask;
    }
}
