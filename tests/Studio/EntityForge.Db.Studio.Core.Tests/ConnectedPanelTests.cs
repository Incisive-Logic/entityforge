using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Client;
using EntityForge.Query.Projection;
using EntityForge.Db.Studio.Workspace;
using EntityForge.UI.Workspace;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Studio.Core.Tests;

[TestClass]
public sealed class ConnectedPanelTests
{
    private static readonly PanelId Q1 = new("data", "q1"), Q2 = new("other", "q2"),
        R1 = new("data", "r1"), R2 = new("data", "r2"), E = new("data", "e");

    [TestMethod]
    public async Task Evidence_and_exploration_survive_paging_but_reset_on_a_new_query()
    {
        using var sessions = new StudioPanelSessions();
        sessions.Configure(Nodes, Graph([new(R1, Q1), new(E, R1)]));
        var query = sessions.QueryFor(Q1)!.Query;
        query.Bind(new Paged());
        await query.ExecuteAsync(Document());
        var selection = sessions.SelectionFor(R1)!;
        var page = query.Result!;
        var details = Result();
        var entity = new EntityProjectionNode(new EntityForge.Query.Values.IdentifierValue("urn:company"), [], [], []);
        selection.Exploration.Open(page, details, entity);
        selection.Exploration.Current(page).ScrollOffset = 100;
        sessions.Select(R1, details, entity);
        selection.ExpandedEvidence.Add("provider");
        await query.NextPageAsync();
        Assert.AreSame(details, sessions.SelectionFor(E)!.Result);
        Assert.AreSame(entity, selection.Entity);
        Assert.IsTrue(selection.ExpandedEvidence.Contains("provider"));
        query.PreviousPage();
        Assert.AreEqual(100d, sessions.SelectionFor(R1)!.Exploration.Current(page).ScrollOffset);
        await query.ExecuteAsync(Document());
        Assert.IsNull(sessions.SelectionFor(E)!.Entity);
        Assert.AreEqual(0, selection.ExpandedEvidence.Count);
    }

    private sealed class Paged : IPagedEntityQueryExecutor
    {
        public ValueTask<EntityProjectionQueryResult> ExecuteAsync(SemanticQueryDocument query, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result() with
            {
                NextContinuation = "next"
            });

        public ValueTask<EntityProjectionQueryResult> ExecutePageAsync(SemanticQueryDocument query, string after, long snapshotVersion,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Result() with
            {
                NextContinuation = "next"
            });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Evicted_page_exploration_is_released_with_or_without_panel_refresh(bool refresh)
    {
        using var sessions = new StudioPanelSessions();
        sessions.Configure(Nodes, Graph([new(R1, Q1)]));
        var query = sessions.QueryFor(Q1)!.Query;
        query.Bind(new Paged());
        await query.ExecuteAsync(Document());
        var selection = sessions.SelectionFor(R1)!;
        var first = query.Result!;
        var oldView = selection.Exploration.Current(first);
        for (int i = 0; i < 10; i++)
        {
            await query.NextPageAsync();
            if (refresh)
            {
                sessions.SelectionFor(R1);
            }
        }
        Assert.IsFalse(query.ContainsResult(first));
        if (refresh)
        {
            Assert.AreSame(query.Result, selection.Anchor);
        }
        else
        {
            Assert.IsNull(selection.Anchor);
        }

        Assert.AreNotSame(oldView, selection.Exploration.Current(first));
    }

    private static readonly (PanelId Id, string Key)[] Nodes = [(Q1, StudioModuleKeys.Query),
        (Q2, StudioModuleKeys.Query), (R1, StudioModuleKeys.Results), (R2, StudioModuleKeys.Results), (E, StudioModuleKeys.Evidence)];

    private static PanelConnections Graph(IEnumerable<PanelConnection>? links = null) => new(
        Nodes.Select(n => new ConnectedPanel(n.Id, n.Id.ModuleId, StudioPanelContracts.For(n.Key))), links);

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    public void New_panel_connects_only_to_one_compatible_source_on_its_tab(int count, bool expected)
    {
        var target = new ConnectedPanel(R1, "Results", StudioPanelContracts.For(StudioModuleKeys.Results));
        var sources = Enumerable.Range(0, count).Select(i => new ConnectedPanel(new PanelId("data", $"query-{i}"),
            $"Query {i}", StudioPanelContracts.For(StudioModuleKeys.Query)));
        var otherTab = new ConnectedPanel(Q2, "Other Query", StudioPanelContracts.For(StudioModuleKeys.Query));
        var graph = new PanelConnections(sources.Append(target).Append(otherTab));
        Assert.IsNull(graph.Source(R1));
        Assert.AreEqual(expected, graph.ConnectUniqueSourceOnTab(R1));
        Assert.AreEqual(expected, graph.Source(R1) is not null);
        if (expected)
        {
            Assert.AreEqual("data", graph.Source(R1)!.TabId);
        }

        graph.Connect(R1, Q2);
        Assert.IsFalse(graph.ConnectUniqueSourceOnTab(R1));
        Assert.AreEqual(Q2, graph.Source(R1));
    }

    [TestMethod]
    public void Evidence_auto_connection_uses_results_and_restoration_never_auto_connects()
    {
        var graph = Graph();
        Assert.IsFalse(graph.ConnectUniqueSourceOnTab(E)); // Two Results panels.
        graph = new PanelConnections(graph.Panels.Where(p => p.Id != R2));
        Assert.IsTrue(graph.ConnectUniqueSourceOnTab(E));
        Assert.AreEqual(R1, graph.Source(E));
        graph.Disconnect(E);
        var restored = new PanelConnections(graph.Panels, graph.Capture());
        Assert.IsNull(restored.Source(E));
        Assert.IsNull(restored.Source(R1));
    }

    [TestMethod]
    public void Capabilities_and_cycles_are_checked_and_missing_sources_are_not_replaced()
    {
        var graph = Graph();
        Assert.IsNull(graph.Source(R1));
        Assert.IsTrue(graph.CanConnect(R1, Q2));
        Assert.IsFalse(graph.CanConnect(E, Q1));
        Assert.IsFalse(graph.CanConnect(Q1, Q1));
        graph.Connect(R1, Q1);
        graph.Connect(E, R1);
        var removed = new PanelConnections(graph.Panels.Where(p => p.Id != Q1), graph.Capture());
        Assert.IsNull(removed.Source(R1));
        Assert.AreEqual(R1, removed.Source(E));
        var changed = new PanelConnections(graph.Panels.Select(p => p.Id == Q1
            ? p with
            {
                Contract = StudioPanelContracts.For(StudioModuleKeys.Evidence)
            } : p), graph.Capture());
        Assert.IsNull(changed.Source(R1));
        var contract = new PanelContract("value", ["value"]);
        var cycle = new PanelConnections([new(Q1, "A", contract), new(Q2, "B", contract)]);
        cycle.Connect(Q1, Q2);
        Assert.IsFalse(cycle.CanConnect(Q2, Q1));
    }

    [TestMethod]
    public void Connections_and_names_round_trip_and_survive_tab_changes()
    {
        var session = new StudioWorkspaceSession(StudioWorkspaceDefaults.Create());
        session.SetPanelConnections([new(R1, Q2), new(E, R1)]);
        session.NamePanel(Q2, "Companies");
        session.Rename("data", "Investigation");
        session.Select("operations");
        var restored = WorkspaceDocumentSerializer.Deserialize(WorkspaceDocumentSerializer.Serialize(session.Document));
        CollectionAssert.AreEqual(session.Document.PanelConnections.ToArray(), restored.PanelConnections.ToArray());
        Assert.AreEqual("Companies", restored.PanelNames.Single().Name);
        Assert.AreEqual("operations", restored.SelectedTabId);
        var defaults = WorkspaceDocumentSerializer.Deserialize(WorkspaceDocumentSerializer.Serialize(StudioWorkspaceDefaults.Create()));
        CollectionAssert.AreEqual(StudioWorkspaceDefaults.Create().PanelConnections.ToArray(), defaults.PanelConnections.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Layouts_saved_before_panel_connections_load_unconnected(bool explicitNull)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(WorkspaceDocumentSerializer.Serialize(StudioWorkspaceDefaults.Create()))!;
        if (explicitNull)
        {
            json["panelConnections"] = null;
            json["panelNames"] = null;
        }
        else
        {
            json.AsObject().Remove("panelConnections");
            json.AsObject().Remove("panelNames");
        }
        var restored = WorkspaceDocumentSerializer.Deserialize(json.ToJsonString());
        Assert.AreEqual(0, restored.PanelConnections.Count);
        Assert.AreEqual(0, restored.PanelNames.Count);
        Assert.AreEqual(StudioWorkspaceDefaults.Create().Tabs.Count, restored.Tabs.Count);
    }

    [TestMethod]
    public async Task Rebinding_results_does_not_follow_an_old_inflight_query()
    {
        using var sessions = new StudioPanelSessions();
        var links = Graph([new(R1, Q1), new(R2, Q1), new(E, R1)]);
        sessions.Configure(Nodes, links);
        var first = sessions.QueryFor(Q1)!;
        var second = sessions.QueryFor(Q2)!;
        Assert.AreNotSame(first, second);
        var delayed = new Delayed();
        var pending = first.Query.ExecuteAsync(delayed, Document()).AsTask();
        links.Connect(R1, Q2);
        sessions.Configure(Nodes, links);
        delayed.Completion.SetResult(Result());
        await pending;
        Assert.AreSame(second, sessions.QueryFor(R1));
        Assert.IsNull(sessions.QueryFor(R1)!.Query.Result);
        Assert.AreSame(first.Query.Result, sessions.QueryFor(R2)!.Query.Result);
        Assert.AreSame(second, sessions.QueryFor(E));
        Assert.AreSame(first, sessions.QueryFor(Q1));
    }

    [TestMethod]
    public void Each_results_panel_owns_selection_and_disconnect_stops_evidence_flow()
    {
        using var sessions = new StudioPanelSessions();
        var links = Graph([new(R1, Q1), new(R2, Q1), new(E, R1)]);
        sessions.Configure(Nodes, links);
        Assert.AreNotSame(sessions.SelectionFor(R1), sessions.SelectionFor(R2));
        Assert.AreSame(sessions.SelectionFor(R1), sessions.SelectionFor(E));
        links.Connect(R1, Q2);
        sessions.Configure(Nodes, links);
        Assert.IsNull(sessions.SelectionFor(E)!.Result);
        links.Disconnect(R1);
        sessions.Configure(Nodes, links);
        Assert.IsNull(sessions.QueryFor(E));
    }

    [TestMethod]
    public async Task Removing_query_cancels_execution_and_readding_identity_creates_fresh_state()
    {
        using var sessions = new StudioPanelSessions();
        var links = Graph([new(R1, Q1)]);
        sessions.Configure(Nodes, links);
        var old = sessions.QueryFor(Q1)!;
        var executor = new Delayed();
        var pending = old.Query.ExecuteAsync(executor, Document()).AsTask();
        sessions.Configure(Nodes.Where(n => n.Id != Q1), new PanelConnections(links.Panels.Where(p => p.Id != Q1), links.Capture()));
        Assert.IsTrue(executor.Token.IsCancellationRequested);
        executor.Completion.SetResult(Result());
        await pending;
        Assert.IsNull(old.Query.Result);
        sessions.Configure(Nodes, Graph());
        Assert.AreNotSame(old, sessions.QueryFor(Q1));
        Assert.IsNull(sessions.QueryFor(R1));
    }

    private static SemanticQueryDocument Document() => new(new("https://example.test/C", "C"), [], []);

    [TestMethod]
    public async Task Query_mode_switch_clears_connected_evidence_without_affecting_other_query_panels()
    {
        using var sessions = new StudioPanelSessions();
        sessions.Configure(Nodes, Graph([new(R1, Q1), new(E, R1)]));
        var state = sessions.QueryFor(Q1)!;
        var executor = new Delayed();
        var pending = state.Query.ExecuteAsync(executor, Document()).AsTask();
        executor.Completion.SetResult(Result());
        await pending;
        Assert.AreSame(state.Query.Result, state.ActiveResult);
        state.SetGraphQl(true);
        Assert.IsNull(state.ActiveResult);
        Assert.IsNull(sessions.SelectionFor(E)!.Result);
        Assert.IsFalse(sessions.QueryFor(Q2)!.UseGraphQl);
        state.SetGraphQl(false);
        Assert.AreSame(state.Query.Result, state.ActiveResult);
    }

    private static EntityProjectionQueryResult Result() => new(new EntityProjection(Guid.NewGuid(), [], []), new DatabaseVersion(1));

    [TestMethod]
    public async Task Switching_modes_preserves_both_drafts_and_rejects_cancelled_builder_completion()
    {
        using var sessions = new StudioPanelSessions();
        sessions.Configure(Nodes, Graph([new(R1, Q1), new(E, R1)]));
        var state = sessions.QueryFor(Q1)!;
        var delayed = new Delayed();
        state.Query.Bind(delayed);
        var draft = Document();
        state.Native.Draft = "query { company }";
        state.Native.Variables = "{\"first\":10}";
        var pending = state.Query.ExecuteAsync(draft).AsTask();
        state.SetGraphQl(true);
        Assert.IsTrue(delayed.Token.IsCancellationRequested);
        Assert.AreEqual(EntityQueryExecutionState.Cancelled, state.Query.State);
        state.SetGraphQl(false);
        Assert.AreSame(draft, state.Query.Draft);
        Assert.AreEqual("query { company }", state.Native.Draft);
        Assert.AreEqual("{\"first\":10}", state.Native.Variables);
        delayed.Completion.SetResult(Result());
        await pending;
        Assert.IsNull(state.ActiveResult);
        Assert.IsNull(sessions.SelectionFor(E)!.Result);
    }

    [TestMethod]
    public async Task Refresh_preserves_selection_until_recovery_invalidates_results()
    {
        using var sessions = new StudioPanelSessions();
        sessions.Configure(Nodes, Graph([new(R1, Q1), new(E, R1)]));
        sessions.Bind(new Paged(), null);
        var state = sessions.QueryFor(Q1)!;
        var draft = Document();
        await state.Query.ExecuteAsync(draft);
        var result = state.Query.Result!;
        var entity = new EntityProjectionNode(new EntityForge.Query.Values.IdentifierValue("urn:company"), [], [], []);
        sessions.SelectionFor(R1);
        sessions.Select(R1, result, entity);
        sessions.RefreshBindings(new Paged(), null, invalidateResults: false);
        Assert.AreSame(result, state.Query.Result);
        Assert.AreSame(entity, sessions.SelectionFor(E)!.Entity);
        sessions.RefreshBindings(new Paged(), null, invalidateResults: true);
        Assert.AreSame(draft, state.Query.Draft);
        Assert.IsNull(state.Query.Result);
        Assert.IsNull(sessions.SelectionFor(E)!.Entity);
    }

    private sealed class Delayed : IEntityQueryExecutor
    {
        public TaskCompletionSource<EntityProjectionQueryResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token
        {
            get; private set;
        }

        public ValueTask<EntityProjectionQueryResult> ExecuteAsync(SemanticQueryDocument query, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            return new(Completion.Task);
        }
    }
}
