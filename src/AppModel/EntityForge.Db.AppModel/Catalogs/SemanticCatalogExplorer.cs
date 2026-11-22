using EntityForge.Db.Client;

namespace EntityForge.Db.AppModel.Catalogs;

/// <summary>Selects hierarchy or alphabetical presentation of semantic concepts.</summary>
public enum SemanticCatalogView
{
    /// <summary>Shows concepts arranged by their vocabulary hierarchy.</summary>
    Hierarchy,
    /// <summary>Shows concepts in alphabetical order.</summary>
    Alphabetical
}

/// <summary>Represents a visible concept and its indentation in the catalog explorer.</summary>
/// <param name="Concept">The semantic concept displayed by this row.</param>
/// <param name="Depth">The hierarchy indentation depth.</param>
public sealed record SemanticCatalogConceptRow(SemanticCatalogConcept Concept, int Depth);

/// <summary>UI-neutral selection and filtering state for a resolved Semantic catalogue.</summary>
public sealed class SemanticCatalogExplorer
{
    private SemanticCatalog? _catalog;
    private string _filter = String.Empty;

    /// <summary>Occurs when catalog, filter, selection or view state changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the catalog currently displayed.</summary>
    public SemanticCatalog? Catalog => _catalog;

    /// <summary>Gets the selected hierarchy or alphabetical presentation.</summary>
    public SemanticCatalogView View
    {
        get; private set;
    }

    /// <summary>Gets the current text filter.</summary>
    public string Filter => _filter;

    /// <summary>Gets the selected concept, if any.</summary>
    public SemanticCatalogConcept? SelectedConcept
    {
        get; private set;
    }

    /// <summary>Gets the filtered concept rows in display order.</summary>
    public IReadOnlyList<SemanticCatalogConceptRow> VisibleConcepts { get; private set; } = [];

    /// <summary>Gets whether the current catalog exposes a concept hierarchy.</summary>
    public bool HasHierarchy => _catalog?.Concepts.Any(concept => concept.BroaderConcepts.Any(parent =>
        _catalog.Concepts.Any(candidate => candidate.Identifier.AbsoluteUri == parent.AbsoluteUri))) == true;

    /// <summary>Replaces the catalog and refreshes visible concepts and selection.</summary>
    public void SetCatalog(SemanticCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        SelectedConcept = catalog.Concepts.FirstOrDefault();
        Refresh();
    }

    /// <summary>Changes the text filter and refreshes visible concepts.</summary>
    public void SetFilter(string? filter)
    {
        string normalized = filter?.Trim() ?? String.Empty;
        if (StringComparer.Ordinal.Equals(_filter, normalized))
        {
            return;
        }

        _filter = normalized;
        Refresh();
    }

    /// <summary>Changes between hierarchy and alphabetical presentation.</summary>
    public void SetView(SemanticCatalogView view)
    {
        if (View == view)
        {
            return;
        }

        View = view;
        Refresh();
    }

    /// <summary>Selects a concept in the current explorer state.</summary>
    public void Select(Uri identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        SemanticCatalogConcept selected = _catalog?.Concepts.FirstOrDefault(item => item.Identifier.AbsoluteUri == identifier.AbsoluteUri)
            ?? throw new ArgumentException($"The catalogue does not contain concept '{identifier}'.", nameof(identifier));
        if (ReferenceEquals(SelectedConcept, selected))
        {
            return;
        }

        SelectedConcept = selected;
        OnChanged();
    }

    private void Refresh()
    {
        if (_catalog is null)
        {
            VisibleConcepts = [];
            OnChanged();
            return;
        }

        IEnumerable<SemanticCatalogConceptRow> rows = View == SemanticCatalogView.Alphabetical
            ? _catalog.Concepts.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => new SemanticCatalogConceptRow(item, 0))
            : Hierarchy(_catalog.Concepts);
        if (_filter.Length != 0)
        {
            rows = rows.Where(item => Matches(item.Concept, _filter))
                .Select(item => item with { Depth = 0 });
        }

        VisibleConcepts = rows.ToArray();
        if (SelectedConcept is null || !_catalog.Concepts.Contains(SelectedConcept))
        {
            SelectedConcept = _catalog.Concepts.FirstOrDefault();
        }

        OnChanged();
    }

    private static IReadOnlyList<SemanticCatalogConceptRow> Hierarchy(
        IReadOnlyList<SemanticCatalogConcept> concepts)
    {
        // Semantic identifiers include fragments; System.Uri equality ignores them.
        IReadOnlyDictionary<string, SemanticCatalogConcept> byId = concepts.ToDictionary(
            item => item.Identifier.AbsoluteUri, StringComparer.Ordinal);
        var children = concepts.SelectMany(child => child.BroaderConcepts
                .Where(parent => byId.ContainsKey(parent.AbsoluteUri)).Select(parent => (parent: parent.AbsoluteUri, child)))
            .GroupBy(item => item.parent, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.child)
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray(), StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SemanticCatalogConceptRow>();
        void Add(SemanticCatalogConcept concept, int depth)
        {
            if (!visited.Add(concept.Identifier.AbsoluteUri))
            {
                return;
            }

            result.Add(new SemanticCatalogConceptRow(concept, depth));
            if (children.TryGetValue(concept.Identifier.AbsoluteUri, out SemanticCatalogConcept[]? descendants))
            {
                foreach (SemanticCatalogConcept child in descendants)
                {
                    Add(child, depth + 1);
                }
            }
        }
        foreach (SemanticCatalogConcept root in concepts.Where(item => !item.BroaderConcepts.Any(parent => byId.ContainsKey(parent.AbsoluteUri)))
                     .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            Add(root, 0);
        }

        foreach (SemanticCatalogConcept remaining in concepts.OrderBy(item => item.DisplayName,
                     StringComparer.CurrentCultureIgnoreCase))
        {
            Add(remaining, 0);
        }

        return result;
    }

    private static bool Matches(SemanticCatalogConcept concept, string filter) =>
        concept.DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        concept.Identifier.AbsoluteUri.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        (concept.Description?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
        concept.Properties.Any(item => item.DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase)) ||
        concept.Relationships.Any(item => item.DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase));

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
