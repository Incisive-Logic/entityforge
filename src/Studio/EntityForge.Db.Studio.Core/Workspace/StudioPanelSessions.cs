using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Client;
using EntityForge.Query.Projection;
using EntityForge.UI.Workspace;

namespace EntityForge.Db.Studio.Workspace;

/// <summary>Execution belongs to a Query instance; selection belongs to a Results instance.</summary>
public sealed class StudioPanelSessions : IDisposable
{
    public sealed class QueryState : IDisposable
    {
        public EntityQuerySession Query { get; } = new();

        public NativeQuerySession Native { get; } = new();

        public QueryExecutionFeedback SemanticFeedback { get; } = new();

        public QueryExecutionFeedback NativeFeedback { get; } = new();

        public QueryState()
        {
            Query.Changed += (_, _) => SemanticFeedback.Update(Query.State, Query.IsPaging);
            Native.Changed += (_, _) => NativeFeedback.Update(Native.State, Native.IsPaging);
        }

        public bool UseGraphQl
        {
            get; private set;
        }

        public event Action? ModeChanged;

        public EntityProjectionQueryResult? ActiveResult => UseGraphQl ? Native.Result?.Entities : Query.Result;

        public void SetGraphQl(bool enabled)
        {
            if (UseGraphQl == enabled)
            {
                return;
            }

            Query.Cancel();
            Native.Cancel();
            UseGraphQl = enabled;
            ModeChanged?.Invoke();
        }

        public void Dispose()
        {
            Query.Dispose();
            Native.Dispose();
        }
    }

    public sealed class SelectionState
    {
        public bool ShowResultSyntax
        {
            get; set;
        }

        public EntityExplorationState Exploration { get; } = new();

        public EntityProjectionQueryResult? Anchor
        {
            get; private set;
        }

        public double EvidenceScrollOffset
        {
            get; set;
        }

        public HashSet<string> ExpandedEvidence { get; } = new(StringComparer.Ordinal);

        public EntityProjectionQueryResult? Result
        {
            get; private set;
        }

        public EntityProjectionNode? Entity
        {
            get; private set;
        }

        public void Select(EntityProjectionQueryResult result, EntityProjectionNode entity)
        {
            if (!ReferenceEquals(Result, result) || !ReferenceEquals(Entity, entity))
            {
                EvidenceScrollOffset = 0;
                ExpandedEvidence.Clear();
            }
            Result = result;
            Entity = entity;
        }

        public void SetAnchor(EntityProjectionQueryResult? result) => Anchor = result;

        public void Clear()
        {
            Result = null;
            Entity = null;
            Anchor = null;
            Exploration.Clear();
            EvidenceScrollOffset = 0;
            ExpandedEvidence.Clear();
        }
    }

    private readonly Dictionary<PanelId, QueryState> _queries = [];
    private readonly Dictionary<PanelId, SelectionState> _selections = [];
    private PanelConnections _links = new([]);

    public event Action? Changed;

    public event Action? DraftChanged;

    public void Refresh() => Changed?.Invoke();

    public void Configure(IEnumerable<(PanelId Id, string Key)> panels, PanelConnections links)
    {
        var entries = panels.ToArray();
        var queryIds = entries.Where(p => p.Key == StudioModuleKeys.Query).Select(p => p.Id).ToHashSet();
        foreach (var id in _queries.Keys.Except(queryIds).ToArray())
        {
            _queries[id].Dispose();
            _queries.Remove(id);
        }
        foreach (var id in queryIds.Except(_queries.Keys))
        {
            var state = new QueryState();
            state.Query.Changed += (_, _) => ResultsChanged();
            state.Native.Changed += (_, _) => ResultsChanged();
            state.Query.DraftChanged += (_, _) => DraftChanged?.Invoke();
            state.Native.DraftChanged += (_, _) => DraftChanged?.Invoke();
            state.ModeChanged += ResultsChanged;
            _queries.Add(id, state);
        }
        var resultIds = entries.Where(p => p.Key == StudioModuleKeys.Results).Select(p => p.Id).ToHashSet();
        foreach (var id in _selections.Keys.Except(resultIds).ToArray())
        {
            _selections.Remove(id);
        }

        foreach (var id in resultIds)
        {
            if (!_selections.TryGetValue(id, out var selection))
            {
                _selections.Add(id, new());
            }
            else if (_links.Source(id) != links.Source(id))
            {
                selection.Clear();
            }
        }
        // Keep an independent snapshot so edits to the presenter's graph cannot hide a source change.
        _links = new PanelConnections(links.Panels, links.Capture());
    }

    public QueryState? QueryFor(PanelId id)
    {
        var visited = new HashSet<PanelId>();
        for (PanelId? cursor = id; cursor is not null && visited.Add(cursor); cursor = _links.Source(cursor))
        {
            if (_queries.TryGetValue(cursor, out var query))
            {
                return query;
            }
        }

        return null;
    }

    public SelectionState? SelectionFor(PanelId id)
    {
        var resultId = _selections.ContainsKey(id) ? id : _links.Source(id);
        if (resultId is null || !_selections.TryGetValue(resultId, out var selection))
        {
            return null;
        }

        var state = QueryFor(resultId);
        Reconcile(selection, state);
        selection.SetAnchor(state?.ActiveResult);
        return selection;
    }

    private static void Reconcile(SelectionState selection, QueryState? state)
    {
        bool Retained(EntityProjectionQueryResult page) => state?.UseGraphQl == true
            ? ReferenceEquals(page, state.ActiveResult) : state?.Query.ContainsResult(page) == true;
        if (selection.Anchor is { } anchor && !Retained(anchor))
        {
            selection.Clear();
        }

        selection.Exploration.RetainPages(Retained);
    }

    private void ResultsChanged()
    {
        foreach (var (id, selection) in _selections)
        {
            Reconcile(selection, QueryFor(id));
        }

        Changed?.Invoke();
    }

    public void Bind(IEntityQueryExecutor? semantic, INativeQueryExecutor? native)
    {
        foreach (var state in _queries.Values)
        {
            state.Query.Bind(semantic);
            state.Native.Bind(native);
        }
    }

    public void RefreshBindings(IEntityQueryExecutor? semantic, INativeQueryExecutor? native, bool invalidateResults)
    {
        foreach (var state in _queries.Values)
        {
            state.Query.RefreshBinding(semantic, invalidateResults);
            state.Native.RefreshBinding(native, invalidateResults);
        }
        if (invalidateResults)
        {
            foreach (var selection in _selections.Values)
            {
                selection.Clear();
            }
        }
    }

    public void Select(PanelId results, EntityProjectionQueryResult result, EntityProjectionNode entity)
    {
        _selections[results].Select(result, entity);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        foreach (var state in _queries.Values)
        {
            state.Dispose();
        }

        _queries.Clear();
        _selections.Clear();
    }
}
