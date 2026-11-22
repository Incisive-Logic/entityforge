using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EntityForge.Db.AppModel.Catalogs;
using EntityForge.Db.Client;

namespace EntityForge.Db.AppModel.Avalonia.Catalogs;

public sealed class SemanticCatalogConceptRequestedEventArgs(SemanticCatalogConcept concept) : EventArgs
{
    public SemanticCatalogConcept Concept { get; } = concept;
}

public sealed record SemanticCatalogExplorerViewState(string Filter, SemanticCatalogView View,
    string? SelectedConcept, bool ShowIdentifiers, bool ShowDetail);

/// <summary>A searchable, responsive explorer for the resolved logical Semantic catalogue.</summary>
public sealed class SemanticCatalogExplorerControl : ContentControl
{
    private const double CompactWidth = 620;
    private readonly SemanticCatalogExplorer _explorer = new();
    private bool _compact;
    private bool _showDetail;
    private bool _technical;
    private TextBox? _search;
    private readonly DispatcherTimer _filterDelay;
    private string _pendingFilter = String.Empty;

    public event EventHandler<SemanticCatalogConceptRequestedEventArgs>? QueryConceptRequested;

    public SemanticCatalogExplorerControl()
    {
        _filterDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _filterDelay.Tick += (_, _) =>
        {
            _filterDelay.Stop();
            _explorer.SetFilter(_pendingFilter);
        };
        _explorer.Changed += (_, _) => Rebuild();
        DetachedFromVisualTree += (_, _) => _filterDelay.Stop();
        SizeChanged += (_, args) =>
        {
            bool compact = args.NewSize.Width < CompactWidth;
            if (_compact == compact)
            {
                return;
            }

            _compact = compact;
            if (!compact)
            {
                _showDetail = false;
            }

            Rebuild();
        };
    }

    public void SetCatalog(SemanticCatalog catalog) => _explorer.SetCatalog(catalog);

    public SemanticCatalogExplorerViewState CaptureViewState() => new(_search?.Text ?? _explorer.Filter,
        _explorer.View, _explorer.SelectedConcept?.Identifier.AbsoluteUri, _technical, _showDetail);

    public void RestoreViewState(SemanticCatalogExplorerViewState state)
    {
        _explorer.SetFilter(state.Filter);
        _explorer.SetView(state.View);
        if (_explorer.Catalog?.Concepts.FirstOrDefault(concept => concept.Identifier.AbsoluteUri == state.SelectedConcept) is { } selected)
        {
            _explorer.Select(selected.Identifier);
        }

        _technical = state.ShowIdentifiers;
        _showDetail = state.ShowDetail;
        Rebuild();
    }

    private void Rebuild()
    {
        bool restoreSearchFocus = _search?.IsFocused == true;
        int selectionStart = _search?.SelectionStart ?? 0;
        int selectionEnd = _search?.SelectionEnd ?? 0;
        if (_explorer.Catalog is null)
        {
            Content = Muted("No Semantic catalogue is available.");
            return;
        }

        _search = new TextBox
        {
            PlaceholderText = "Search concepts, properties and relationships…",
            Text = _explorer.Filter
        };
        _search.Classes.Add("ef-search");
        TextBox activeSearch = _search;
        global::Avalonia.Automation.AutomationProperties.SetName(activeSearch, "Search catalogue");
        activeSearch.TextChanged += (_, _) =>
        {
            _pendingFilter = activeSearch.Text ?? String.Empty;
            _filterDelay.Stop();
            _filterDelay.Start();
        };
        var view = new ComboBox
        {
            ItemsSource = Enum.GetValues<SemanticCatalogView>(),
            SelectedItem = _explorer.View,
            MinWidth = 120,
        };
        global::Avalonia.Automation.AutomationProperties.SetName(view, "Catalogue order");
        view.SelectionChanged += (_, _) =>
        {
            if (view.SelectedItem is SemanticCatalogView selected)
            {
                _explorer.SetView(selected);
            }
        };
        var technical = new CheckBox
        {
            Content = "Show identifiers",
            IsChecked = _technical,
            VerticalAlignment = VerticalAlignment.Center,
        };
        technical.IsCheckedChanged += (_, _) =>
        {
            _technical = technical.IsChecked == true;
            Rebuild();
        };
        var toolbar = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _search, new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                ItemSpacing = 10, LineSpacing = 6, Children = { view, technical }
            }
            },
        };

        Control body = _compact
            ? _showDetail && _explorer.SelectedConcept is not null
                ? Detail(_explorer.SelectedConcept, compact: true)
                : ConceptList()
            : new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("240,*"),
                ColumnSpacing = 14,
                Children = { ConceptList(), At(Detail(_explorer.SelectedConcept, compact: false), 1) },
            };
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 12,
            MinHeight = 380,
            Children = { toolbar, AtRow(body, 1) },
        };
        if (restoreSearchFocus)
        {
            activeSearch.Focus();
            activeSearch.SelectionStart = Math.Min(selectionStart, activeSearch.Text?.Length ?? 0);
            activeSearch.SelectionEnd = Math.Min(selectionEnd, activeSearch.Text?.Length ?? 0);
        }
    }

    private Control ConceptList()
    {
        var list = new StackPanel { Spacing = 4 };
        list.Children.Add(Eyebrow($"CONCEPTS · {_explorer.VisibleConcepts.Count}"));
        foreach (SemanticCatalogConceptRow row in _explorer.VisibleConcepts)
        {
            var button = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(9 + row.Depth * 16, 7, 9, 7),
                Content = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    ColumnSpacing = 8,
                    Children =
                    {
                        SemanticText(row.Depth == 0 ? "◇" : "↳"),
                        At(new TextBlock
                        {
                            Text = row.Concept.DisplayName,
                            FontWeight = row.Depth == 0 ? FontWeight.SemiBold : FontWeight.Normal
                        }, 1),
                    },
                },
            };
            button.Classes.Add("ef-catalog-concept");
            if (_explorer.SelectedConcept?.Identifier.AbsoluteUri == row.Concept.Identifier.AbsoluteUri)
            {
                button.Classes.Add("selected");
            }

            button.Click += (_, _) =>
            {
                _explorer.Select(row.Concept.Identifier);
                if (_compact)
                {
                    _showDetail = true;
                    Rebuild();
                }
            };
            list.Children.Add(button);
        }
        if (_explorer.VisibleConcepts.Count == 0)
        {
            list.Children.Add(Muted("No concepts match this search."));
        }

        return new ScrollViewer { Content = list };
    }

    private Control Detail(SemanticCatalogConcept? concept, bool compact)
    {
        if (concept is null)
        {
            return Muted("Select a concept to inspect its logical contract.");
        }

        var content = new StackPanel { Spacing = 14 };
        if (compact)
        {
            var back = Command("← Concepts");
            back.HorizontalAlignment = HorizontalAlignment.Left;
            back.Click += (_, _) =>
            {
                _showDetail = false;
                Rebuild();
            };
            content.Children.Add(back);
        }
        content.Children.Add(new TextBlock { Text = concept.DisplayName, Classes = { "ef-entity-title" } });
        if (!String.IsNullOrWhiteSpace(concept.Description))
        {
            content.Children.Add(Muted(concept.Description));
        }

        if (_technical)
        {
            content.Children.Add(Technical(concept.Identifier.AbsoluteUri));
        }

        var hierarchy = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (Uri item in concept.BroaderConcepts)
        {
            hierarchy.Children.Add(ConceptLink("Broader", item));
        }

        foreach (Uri item in concept.NarrowerConcepts)
        {
            hierarchy.Children.Add(ConceptLink("Narrower", item));
        }

        foreach (Uri item in concept.EquivalentConcepts)
        {
            hierarchy.Children.Add(ConceptLink("Equivalent", item));
        }

        if (hierarchy.Children.Count != 0)
        {
            content.Children.Add(Eyebrow("CONCEPT RELATIONSHIPS"));
            content.Children.Add(hierarchy);
        }

        content.Children.Add(Eyebrow($"PROPERTIES · {concept.Properties.Count}"));
        if (concept.Properties.Count == 0)
        {
            content.Children.Add(Muted("No resolved properties."));
        }

        foreach (SemanticCatalogProperty property in concept.Properties)
        {
            content.Children.Add(Member("·", property.DisplayName,
                $"{ValueKind(property.ValueKind)}  ·  {Cardinality(property.Minimum, property.Maximum)}",
                _technical ? property.Identifier.AbsoluteUri : property.Description));
        }

        content.Children.Add(Eyebrow($"RELATIONSHIPS · {concept.Relationships.Count}"));
        if (concept.Relationships.Count == 0)
        {
            content.Children.Add(Muted("No outgoing relationships."));
        }

        foreach (SemanticCatalogRelationship relationship in concept.Relationships)
        {
            content.Children.Add(Member("→", relationship.DisplayName,
                $"{String.Join(", ", relationship.TargetConcepts.Select(Label))}  ·  {Cardinality(relationship.Minimum, relationship.Maximum)}",
                _technical ? relationship.Identifier.AbsoluteUri : relationship.Description));
        }

        var query = new Button { Content = "Query This Concept", HorizontalAlignment = HorizontalAlignment.Left };
        query.Classes.Add("ef-primary");
        query.Click += (_, _) => QueryConceptRequested?.Invoke(this, new(concept));
        content.Children.Add(query);
        return new ScrollViewer { Content = content };
    }

    private string Label(Uri identifier) => _explorer.Catalog!.Concepts
        .FirstOrDefault(item => item.Identifier.AbsoluteUri == identifier.AbsoluteUri)?.DisplayName ?? Short(identifier.AbsoluteUri);

    private Control ConceptLink(string relationship, Uri identifier)
    {
        bool available = _explorer.Catalog!.Concepts.Any(item => item.Identifier.AbsoluteUri == identifier.AbsoluteUri);
        var link = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    RelationshipLabel(relationship),
                    new TextBlock
                    {
                        Text = Label(identifier), FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                },
            },
            IsEnabled = available,
            Margin = new Thickness(0, 0, 7, 7),
        };
        link.Classes.Add("ef-concept-link");
        if (_technical)
        {
            ToolTip.SetTip(link, identifier.AbsoluteUri);
        }

        link.Click += (_, _) =>
        {
            _explorer.Select(identifier);
            if (_compact)
            {
                _showDetail = true;
            }
        };
        return link;
    }

    private static TextBlock RelationshipLabel(string text)
    {
        var value = new TextBlock { Text = text.ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center };
        value.Classes.Add("ef-concept-relation");
        return value;
    }

    private static Control Member(string icon, string title, string summary, string? detail)
    {
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        text.Children.Add(Muted(summary));
        if (!String.IsNullOrWhiteSpace(detail))
        {
            text.Children.Add(Technical(detail));
        }

        var card = new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(7),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 9,
                Children = { SemanticText(icon), At(text, 1) }
            },
        };
        card.Classes.Add("ef-catalog-member");
        return card;
    }

    private static string ValueKind(EntityForge.Semantics.SemanticValueKind kind) => kind switch
    {
        EntityForge.Semantics.SemanticValueKind.AbsoluteUri => "URI",
        EntityForge.Semantics.SemanticValueKind.DateTime => "Date and time",
        EntityForge.Semantics.SemanticValueKind.LanguageString => "Language string",
        _ => kind.ToString(),
    };

    private static string Cardinality(int minimum, int? maximum) =>
        minimum == 1 && maximum == 1 ? "Required" : $"{minimum}..{(maximum is null ? "*" : maximum.Value)}";

    private static string Short(string value)
    {
        int separator = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('#'));
        return separator >= 0 && separator < value.Length - 1 ? value[(separator + 1)..] : value;
    }

    private static Button Command(string text)
    {
        var value = new Button { Content = text };
        value.Classes.Add("ef-command");
        return value;
    }

    private static TextBlock Eyebrow(string text)
    {
        var value = new TextBlock { Text = text };
        value.Classes.Add("ef-eyebrow");
        return value;
    }

    private static TextBlock Muted(string text)
    {
        var value = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        value.Classes.Add("ef-muted");
        return value;
    }

    private static TextBlock Technical(string text)
    {
        var value = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        value.Classes.Add("ef-technical");
        return value;
    }

    private static TextBlock SemanticText(string text)
    {
        var value = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        value.Classes.Add("ef-catalog-glyph");
        return value;
    }

    private static T At<T>(T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static T AtRow<T>(T control, int row) where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }
}
