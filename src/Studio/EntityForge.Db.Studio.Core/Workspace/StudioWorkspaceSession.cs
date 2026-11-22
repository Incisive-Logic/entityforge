using EntityForge.UI.Workspace;

namespace EntityForge.Db.Studio.Workspace;

/// <summary>Owns user changes to tabs and layouts while keeping the persisted document immutable.</summary>
public sealed class StudioWorkspaceSession
{
    public StudioWorkspaceSession(WorkspaceDocument document) => Document = document;

    public WorkspaceDocument Document
    {
        get; private set;
    }

    public WorkspaceTab SelectedTab => Document.Tabs.Single(tab => tab.Id == Document.SelectedTabId);

    public void Select(string tabId) => Replace(Document.Tabs, tabId);

    public void Rename(string tabId, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Replace(Document.Tabs.Select(tab => tab.Id == tabId ? tab with { Title = title.Trim() } : tab),
            Document.SelectedTabId);
    }

    public WorkspaceTab Add(string title, string iconKey, WorkspaceLayout layout)
    {
        var tab = new WorkspaceTab($"tab-{Guid.NewGuid():N}", title, iconKey, layout);
        Replace(Document.Tabs.Append(tab), tab.Id);
        return tab;
    }

    public void Remove(string tabId)
    {
        if (Document.Tabs.Count == 1)
        {
            throw new InvalidOperationException("Studio must retain at least one tab.");
        }

        WorkspaceTab[] remaining = Document.Tabs.Where(tab => tab.Id != tabId).ToArray();
        if (remaining.Length == Document.Tabs.Count)
        {
            throw new KeyNotFoundException(tabId);
        }

        string selected = Document.SelectedTabId == tabId ? remaining[0].Id : Document.SelectedTabId;
        Replace(remaining, selected);
    }

    public void UpdateSelectedLayout(WorkspaceLayout layout, WorkspaceResponsivePreferences preferences) =>
        Replace(Document.Tabs.Select(tab => tab.Id == Document.SelectedTabId
            ? tab with
            {
                Layout = layout,
                ResponsivePreferences = preferences
            }
            : tab), Document.SelectedTabId);

    public void Reset() => Document = StudioWorkspaceDefaults.Create();

    public void SetPanelConnections(IReadOnlyList<PanelConnection> connections) =>
        Document = Document with
        {
            PanelConnections = connections.ToArray()
        };

    public void NamePanel(PanelId id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Document = Document with
        {
            PanelNames = Document.PanelNames.Where(item => item.Id != id)
            .Append(new PanelName(id, name.Trim())).ToArray()
        };
    }

    public void ForgetPanel(PanelId id) => Document = Document with
    {
        PanelNames = Document.PanelNames.Where(item => item.Id != id).ToArray(),
        PanelConnections = Document.PanelConnections.Where(link => link.Source != id && link.Target != id).ToArray(),
    };

    private void Replace(IEnumerable<WorkspaceTab> tabs, string selectedTabId) =>
        Document = new WorkspaceDocument(tabs.ToArray(), selectedTabId)
        {
            PanelConnections = Document.PanelConnections,
            PanelNames = Document.PanelNames
        };
}
