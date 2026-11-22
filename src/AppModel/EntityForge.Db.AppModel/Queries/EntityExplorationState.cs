using EntityForge.Db.Client;
using EntityForge.Query.Projection;

namespace EntityForge.Db.AppModel.Queries;

/// <summary>Per-results-panel navigation with at most 32 views per page, including its root.
/// Owners must call <see cref="RetainPages"/> when their retained query pages change.</summary>
public sealed class EntityExplorationState
{
    /// <summary>Retains one entity or evidence view and its presentation state within a query-results history.</summary>
    public sealed class View(EntityProjectionQueryResult result, EntityProjectionNode? entity = null, bool evidence = false)
    {
        /// <summary>Gets the query result supplying this view's data.</summary>
        public EntityProjectionQueryResult Result { get; } = result;

        /// <summary>Gets the selected entity, or null for the root result view.</summary>
        public EntityProjectionNode? Entity { get; } = entity;

        /// <summary>Gets whether this view presents evidence instead of the ordinary entity view.</summary>
        public bool IsEvidence { get; } = evidence;

        /// <summary>Gets or sets the scroll position to restore when returning to this view.</summary>
        public double ScrollOffset
        {
            get; set;
        }

        /// <summary>Gets the mutable set of expanded presentation-node identifiers.</summary>
        public HashSet<string> Expanded { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<EntityProjectionQueryResult, List<View>> _pages = new(ReferenceEqualityComparer.Instance);

    /// <summary>Gets the current view for a retained root result page, creating its root history if needed.</summary>
    public View Current(EntityProjectionQueryResult page) => History(page)[^1];

    /// <summary>Checks whether the page has a prior exploration view.</summary>
    public bool CanGoBack(EntityProjectionQueryResult page) => History(page).Count > 1;

    /// <summary>Adds an entity view to the page's bounded navigation history.</summary>
    public void Open(EntityProjectionQueryResult page, EntityProjectionQueryResult result, EntityProjectionNode entity) =>
        Add(page, new(result, entity));

    /// <summary>Adds an evidence view to the page's bounded navigation history.</summary>
    public void OpenEvidence(EntityProjectionQueryResult page, EntityProjectionQueryResult result, EntityProjectionNode entity) =>
        Add(page, new(result, entity, evidence: true));

    private void Add(EntityProjectionQueryResult page, View view)
    {
        var history = History(page);
        if (history.Count == 32)
        {
            history.RemoveAt(1);
        }

        history.Add(view);
    }

    /// <summary>Releases navigation histories whose root pages are no longer retained by the owner.</summary>
    /// <param name="retain">Returns true for each root page whose history should remain available.</param>
    public void RetainPages(Func<EntityProjectionQueryResult, bool> retain)
    {
        ArgumentNullException.ThrowIfNull(retain);
        foreach (var page in _pages.Keys.Where(page => !retain(page)).ToArray())
        {
            _pages.Remove(page);
        }
    }

    /// <summary>Returns to the preceding view when one exists.</summary>
    public void Back(EntityProjectionQueryResult page)
    {
        var history = History(page);
        if (history.Count > 1)
        {
            history.RemoveAt(history.Count - 1);
        }
    }

    /// <summary>Releases all retained exploration histories.</summary>
    public void Clear() => _pages.Clear();

    private List<View> History(EntityProjectionQueryResult page)
    {
        if (!_pages.TryGetValue(page, out var history))
        {
            _pages.Add(page, history = [new(page)]);
        }

        return history;
    }
}
