using Avalonia.Controls;
using Avalonia.Layout;
using EntityForge.Db.AppModel.Connections;
using EntityForge.Db.AppModel.Optimizations;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Studio.Modules;
using EntityForge.Db.Studio.Workspace;
using EntityForge.UI.Avalonia.Workspace;
using EntityForge.UI.Workspace;

namespace EntityForge.Db.Studio.Avalonia;

internal sealed class StudioConnectedPanels(
    StudioModuleRegistry registry, EntityForgeConnectionSession connection,
    SemanticPropertyOptimizationSession optimizer, EntityForge.Db.AppModel.Storage.StorageProfileSession storage, StudioHostServices services) : IDisposable
{
    private readonly NativeCatalogueSession _catalogue = new();
    private readonly StudioPanelSessions _sessions = new();
    private readonly StudioPanelSessions.QueryState _empty = new();
    private readonly Dictionary<PanelId, StudioModuleViewFactory> _views = [];
    private readonly Dictionary<PanelId, StudioPanelViewState> _viewStates = [];
    private WorkspaceDocument _document = StudioWorkspaceDefaults.Create();

    public ConnectedPanelPresenter Presenter { get; } = new();

    public StudioPanelSessions Sessions => _sessions;

    public void Configure(WorkspaceDocument document)
    {
        _document = document;
        var nodes = document.Tabs.SelectMany(tab => WorkspaceLayoutTraversal.GetModules(tab.Layout)
            .Select(node => (Id: new PanelId(tab.Id, node.Id), Node: node, Tab: tab))).ToArray();
        var usedNames = document.PanelNames.Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var panels = nodes.Select(item =>
        {
            string? title = document.PanelNames.FirstOrDefault(n => n.Id == item.Id)?.Name;
            if (title is null)
            {
                int number = 1;
                do
                {
                    title = registry.Get(item.Node.ModuleKey).DisplayName + " " + number++;
                }
                while (!usedNames.Add(title));
            }
            return new ConnectedPanel(item.Id, title, StudioPanelContracts.For(item.Node.ModuleKey)) { Location = item.Tab.Title };
        }).ToArray();
        Presenter.Connections = new PanelConnections(panels, document.PanelConnections);
        _sessions.Configure(nodes.Select(n => (n.Id, n.Node.ModuleKey)), Presenter.Connections);
        _sessions.Bind(connection.QueryExecutor, connection.NativeQueryExecutor);
        foreach (var id in _views.Keys.Where(id => !panels.Any(p => p.Id == id)).ToArray())
        {
            _views.Remove(id);
        }

        foreach (var id in _viewStates.Keys.Where(id => !panels.Any(p => p.Id == id)).ToArray())
        {
            _viewStates.Remove(id);
        }
    }

    public void CaptureViewState()
    {
        foreach (var view in _views.Values)
        {
            view.CaptureViewState();
        }
    }

    public void RefreshDraftWarnings()
    {
        foreach (var view in _views.Values)
        {
            view.RefreshDraftWarnings();
        }
    }

    public void Bind(bool connected)
    {
        _viewStates.Clear();
        _sessions.Bind(connected ? connection.QueryExecutor : null, connected ? connection.NativeQueryExecutor : null);
        _catalogue.Bind(connected ? connection.NativeQueryExecutor : null);
    }

    public void RefreshBindings(bool invalidateResults)
    {
        _sessions.RefreshBindings(connection.QueryExecutor, connection.NativeQueryExecutor, invalidateResults);
        _catalogue.RefreshBinding(connection.NativeQueryExecutor, invalidateResults);
    }

    public Control Create(WorkspaceModuleNode node)
    {
        var id = new PanelId(_document.SelectedTabId, node.Id);
        var panel = Presenter.Connections.Panels.FirstOrDefault(p => p.Id == id);
        // Layout editing may ask for the new node before the host captures the layout.
        if (panel is null)
        {
            return new TextBlock { Text = registry.Get(node.ModuleKey).DisplayName };
        }

        var state = _sessions.QueryFor(id) ?? _empty;
        if (!_viewStates.TryGetValue(id, out var viewState))
        {
            _viewStates.Add(id, viewState = new());
        }

        var factory = new StudioModuleViewFactory(connection, state.Query, state.Native, optimizer, storage)
        {
            ViewState = viewState,
            QueryState = state,
            NativeCatalogue = _catalogue,
            HostServices = services,
            Selection = _sessions.SelectionFor(id),
            EntitySelected = (result, entity) => _sessions.Select(id, result, entity),
            RefreshRequested = _sessions.Refresh,
            QueryConceptRequested = concept => ShowQueryTarget(concept),
            NativeBrowseRequested = text => ShowQueryTarget(null, text),
        };
        _views[id] = factory;
        var descriptor = registry.Get(node.ModuleKey);
        WorkspacePanelStatus? status = null;
        bool diagnostic = node.ModuleKey is StudioModuleKeys.Status or StudioModuleKeys.Security or StudioModuleKeys.Transactions or StudioModuleKeys.Storage or StudioModuleKeys.Optimizer or StudioModuleKeys.Telemetry;
        if (!diagnostic && descriptor.RequiresConnection && connection.Current is null)
        {
            status = new("Database Connection Required", "Connect to a database to use this panel.");
        }
        else if (!diagnostic && descriptor.RequiresConnection && connection.Availability?.Available == false)
        {
            status = new("Connection Unavailable", connection.Error ?? "Waiting for the database to become ready.");
        }
        else if (panel.Contract.Accepts is not null && Presenter.Connections.Source(id) is null)
        {
            status = new("Source Connection Required", "Connect a source panel to continue.");
        }
        else if (panel.Contract.Accepts is not null && _sessions.QueryFor(id) is null)
        {
            status = new("Source Unavailable", "Connect the source panel to continue.");
        }

        Control content = status is null ? factory.Create(node) : new Grid();
        return Presenter.Present(panel, descriptor.DisplayName, content, status);
    }

    public event Action<Control>? QueryTargetRequested;

    private void ShowQueryTarget(SemanticQueryConcept? concept, string? nativeDraft = null)
    {
        var targets = Presenter.Connections.Panels.Where(p => p.Contract.Provides.Contains("query-results")).ToArray();
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Open In Query Panel" });
        foreach (var target in targets)
        {
            var button = new Button { Content = target.Name, HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) =>
            {
                var state = _sessions.QueryFor(target.Id)!;
                state.SetGraphQl(false);
                if (concept is not null)
                {
                    state.Query.Prepare(new SemanticQueryDocument(concept, [], []));
                }
                else if (nativeDraft is not null)
                {
                    state.Native.Draft = nativeDraft;
                    _sessions.Refresh();
                }
            };
            panel.Children.Add(button);
        }
        if (targets.Length == 0)
        {
            panel.Children.Add(new TextBlock { Text = "Add a Query panel using the layout editor." });
        }

        QueryTargetRequested?.Invoke(panel);
    }

    public void Dispose()
    {
        _catalogue.Dispose();
        _sessions.Dispose();
        _empty.Dispose();
        _views.Clear();
        _viewStates.Clear();
    }
}
