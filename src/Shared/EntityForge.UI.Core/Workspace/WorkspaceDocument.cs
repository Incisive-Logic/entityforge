using System.Text.Json;

namespace EntityForge.UI.Workspace;

/// <summary>A versioned set of named workspace tabs independent of any UI framework.</summary>
public sealed record WorkspaceDocument
{
    public const int CurrentFormatVersion = 1;

    public WorkspaceDocument(IReadOnlyList<WorkspaceTab> tabs, string selectedTabId,
        int formatVersion = CurrentFormatVersion)
    {
        Tabs = tabs?.ToArray() ?? throw new ArgumentNullException(nameof(tabs));
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedTabId);
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion));
        }

        if (Tabs.Count == 0)
        {
            throw new ArgumentException("A workspace requires at least one tab.", nameof(tabs));
        }

        if (Tabs.Select(tab => tab.Id).Distinct(StringComparer.Ordinal).Count() != Tabs.Count)
        {
            throw new ArgumentException("Workspace tab identifiers must be unique.", nameof(tabs));
        }

        if (!Tabs.Any(tab => StringComparer.Ordinal.Equals(tab.Id, selectedTabId)))
        {
            throw new ArgumentException("The selected workspace tab does not exist.", nameof(selectedTabId));
        }

        SelectedTabId = selectedTabId;
        FormatVersion = formatVersion;
    }

    public int FormatVersion
    {
        get;
    }

    public IReadOnlyList<WorkspaceTab> Tabs
    {
        get;
    }

    public string SelectedTabId
    {
        get;
    }

    private readonly IReadOnlyList<PanelConnection> _panelConnections = [];
    private readonly IReadOnlyList<PanelName> _panelNames = [];

    public IReadOnlyList<PanelConnection> PanelConnections
    {
        get => _panelConnections;
        init => _panelConnections = value?.ToArray() ?? [];
    }

    public IReadOnlyList<PanelName> PanelNames
    {
        get => _panelNames;
        init => _panelNames = value?.ToArray() ?? [];
    }
}

public sealed record PanelName(PanelId Id, string Name);

/// <summary>A user-nameable tab containing one canonical module layout and independent module state.</summary>
public sealed record WorkspaceTab
{
    public WorkspaceTab(string id, string title, string iconKey, WorkspaceLayout layout,
        WorkspaceResponsivePreferences? responsivePreferences = null,
        IReadOnlyDictionary<string, JsonElement>? moduleState = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(iconKey);
        Id = id;
        Title = title;
        IconKey = iconKey;
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        ResponsivePreferences = responsivePreferences ?? new();
        ModuleState = moduleState ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    }

    public string Id
    {
        get; init;
    }

    public string Title
    {
        get; init;
    }

    public string IconKey
    {
        get; init;
    }

    public WorkspaceLayout Layout
    {
        get; init;
    }

    public WorkspaceResponsivePreferences ResponsivePreferences
    {
        get; init;
    }

    public IReadOnlyDictionary<string, JsonElement> ModuleState
    {
        get; init;
    }
}
