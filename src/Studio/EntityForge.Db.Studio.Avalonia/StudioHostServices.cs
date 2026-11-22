using EntityForge.Db.AppModel.Connections;
using EntityForge.Db.Studio.Workspace;

namespace EntityForge.Db.Studio.Avalonia;

/// <summary>Describes capabilities supplied by the desktop or browser host without leaking host checks into modules.</summary>
public sealed record StudioHostCapabilities(
    bool IsBrowserSandbox,
    bool HasPersistentPreferences,
    bool CanSelectLocalFiles,
    bool CanAccessArbitraryFilePaths);

/// <summary>Supplies host-specific capabilities and persistence boundaries to the shared Studio view.</summary>
public sealed record StudioHostServices(
    StudioHostCapabilities Capabilities,
    IStudioWorkspaceStore Workspaces,
    IEntityForgeConnectionProfileStore ConnectionProfiles,
    Uri DefaultEndpoint)
{
    /// <summary>Consumes a transient reader token supplied by the local sample launcher; never persists it.</summary>
    public Func<string?>? TakeDemoToken
    {
        get; init;
    }

    public global::EntityForge.Db.Studio.Queries.QueryPresetLibrary QueryPresets
    {
        get; init;
    }
        = new(new global::EntityForge.Db.Studio.Queries.MemoryQueryPresetStore());

    public Func<string, Task>? ExportQueryPresets
    {
        get; init;
    }

    public Func<Task<string?>>? ImportQueryPresets
    {
        get; init;
    }

    public global::EntityForge.Db.Studio.Settings.IStudioClientSettingsStore ClientSettings
    {
        get; init;
    }
        = new global::EntityForge.Db.Studio.Settings.MemoryStudioClientSettingsStore();

    /// <summary>Creates transient sandbox services used until browser persistence is configured.</summary>
    public static StudioHostServices CreateTransient(Uri? defaultEndpoint = null) => new(
        new(true, false, true, false),
        new MemoryStudioWorkspaceStore(),
        new MemoryConnectionProfileStore(),
        defaultEndpoint ?? new Uri("http://127.0.0.1:5080"));

    /// <summary>Creates desktop services backed by application-local JSON files.</summary>
    public static StudioHostServices CreateDesktop(string applicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        return new(
            new(false, true, true, true),
            new JsonFileStudioWorkspaceStore(Path.Combine(applicationDataDirectory, "workspace.json")),
            new JsonEntityForgeConnectionProfileStore(Path.Combine(applicationDataDirectory, "connections.json")),
            new Uri("http://127.0.0.1:5080"))
        {
            ClientSettings = new global::EntityForge.Db.Studio.Settings.JsonStudioClientSettingsStore(
                Path.Combine(applicationDataDirectory, "settings.json")),
            QueryPresets = new(new global::EntityForge.Db.Studio.Queries.FileQueryPresetStore(Path.Combine(applicationDataDirectory, "query-presets.json")))
        };
    }

    private sealed class MemoryStudioWorkspaceStore : IStudioWorkspaceStore
    {
        private string? _document;

        public ValueTask<global::EntityForge.UI.Workspace.WorkspaceDocument?> LoadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_document is null ? null :
                global::EntityForge.UI.Workspace.WorkspaceDocumentSerializer.Deserialize(_document));

        public ValueTask SaveAsync(
            global::EntityForge.UI.Workspace.WorkspaceDocument document,
            CancellationToken cancellationToken = default)
        {
            _document = global::EntityForge.UI.Workspace.WorkspaceDocumentSerializer.Serialize(document);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryConnectionProfileStore : IEntityForgeConnectionProfileStore
    {
        private IReadOnlyList<EntityForgeConnectionProfile> _profiles = [];

        public ValueTask<IReadOnlyList<EntityForgeConnectionProfile>> LoadAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(_profiles);

        public ValueTask SaveAsync(
            IReadOnlyList<EntityForgeConnectionProfile> profiles,
            CancellationToken cancellationToken = default)
        {
            _profiles = profiles.ToArray();
            return ValueTask.CompletedTask;
        }
    }
}
