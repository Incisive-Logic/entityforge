namespace EntityForge.UI.Workspace;

/// <summary>A panel identity remains stable when its layout is rearranged.</summary>
public sealed record PanelId(string TabId, string ModuleId);

public sealed record PanelConnection(PanelId Target, PanelId Source);

public sealed record PanelContract(string? Accepts, IReadOnlyList<string> Provides)
{
    public string? SourceTypeLabel
    {
        get; init;
    }
}

public sealed record ConnectedPanel(PanelId Id, string Name, PanelContract Contract)
{
    public string? Location
    {
        get; init;
    }
}

/// <summary>Explicit, capability-checked panel links. No source is chosen implicitly.</summary>
public sealed class PanelConnections
{
    private readonly Dictionary<PanelId, ConnectedPanel> _panels;
    private readonly Dictionary<PanelId, PanelId> _sources = [];

    public PanelConnections(IEnumerable<ConnectedPanel> panels, IEnumerable<PanelConnection>? connections = null)
    {
        _panels = panels.ToDictionary(panel => panel.Id);
        foreach (var link in connections ?? [])
        {
            if (CanConnect(link.Target, link.Source))
            {
                _sources[link.Target] = link.Source;
            }
        }
    }

    public IReadOnlyCollection<ConnectedPanel> Panels => _panels.Values;

    public PanelId? Source(PanelId target) => _sources.GetValueOrDefault(target);

    public IReadOnlyList<PanelConnection> Capture() => _sources.Select(pair => new PanelConnection(pair.Key, pair.Value)).ToArray();

    public bool CanConnect(PanelId target, PanelId source)
    {
        if (target == source || !_panels.TryGetValue(target, out var consumer) ||
            !_panels.TryGetValue(source, out var provider) || consumer.Contract.Accepts is not { } accepts ||
            !provider.Contract.Provides.Contains(accepts, StringComparer.Ordinal))
        {
            return false;
        }

        var visited = new HashSet<PanelId> { target };
        for (PanelId? cursor = source; cursor is not null; cursor = Source(cursor))
        {
            if (!visited.Add(cursor))
            {
                return false;
            }
        }

        return true;
    }

    public void Connect(PanelId target, PanelId source)
    {
        if (!CanConnect(target, source))
        {
            throw new InvalidOperationException("These panels cannot be connected.");
        }

        _sources[target] = source;
    }

    public void Disconnect(PanelId target) => _sources.Remove(target);

    /// <summary>Used only at panel creation, never when restoring or rearranging a layout.</summary>
    public bool ConnectUniqueSourceOnTab(PanelId target)
    {
        if (Source(target) is not null)
        {
            return false;
        }

        var candidates = Panels.Where(p => p.Id.TabId == target.TabId && CanConnect(target, p.Id)).Take(2).ToArray();
        if (candidates.Length != 1)
        {
            return false;
        }

        Connect(target, candidates[0].Id);
        return true;
    }
}
