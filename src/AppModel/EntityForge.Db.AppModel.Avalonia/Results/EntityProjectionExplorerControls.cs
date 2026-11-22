using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Client;
using EntityForge.Query.Projection;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.AppModel.Avalonia.Results;

/// <summary>Friendly, responsive weakly typed entity results with progressive technical disclosure.</summary>
public sealed class EntityProjectionResultsControl : ContentControl
{
    public event Action<EntityProjectionNode>? InspectEvidenceRequested;

    public event Action<EntityProjectionQueryResult, EntityProjectionNode>? EvidenceRequested;

    public event Action? NavigationChanged;

    public EntityExplorationState? Exploration
    {
        get; set;
    }

    public IEntityExplorer? Explorer
    {
        get; set;
    }

    public Func<bool>? IsCurrent
    {
        get; set;
    }

    private EntityProjectionQueryResult? _page;
    private EntityExplorationState.View? _view;

    public bool ShowSummary { get; set; } = true;

    public bool IsRendering
    {
        get; private set;
    }

    public event Action? RenderingChanged;

    private int _renderVersion;
    private Action? _resumeRendering;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _resumeRendering?.Invoke();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        SetRendering(false);
    }

    private void SetRendering(bool value)
    {
        if (IsRendering == value)
        {
            return;
        }

        IsRendering = value;
        RenderingChanged?.Invoke();
    }

    public void SetResult(EntityProjectionQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int version = ++_renderVersion;
        _resumeRendering = null;
        SetRendering(false);
        _page = result;
        _view = Exploration?.Current(result);
        result = _view?.Result ?? result;
        IReadOnlyDictionary<Uri, EvidenceRecord> evidence = result.Document.Evidence
            .ToDictionary(item => item.Reference.Identifier);
        var content = new StackPanel { Spacing = 12 };
        if (Exploration?.CanGoBack(_page) == true)
        {
            var back = new Button { Content = "← Back", HorizontalAlignment = HorizontalAlignment.Left };
            back.Click += (_, _) =>
            {
                Exploration.Back(_page);
                NavigationChanged?.Invoke();
            };
            content.Children.Add(back);
        }
        if (_view is { IsEvidence: true, Entity: { } evidenceEntity })
        {
            content.Children.Add(Muted($"Evidence for {PreferredTitle(evidenceEntity)} · Snapshot {result.SnapshotVersion.Value:N0}"));
            var inspector = new EntityProjectionEvidenceControl { Expanded = _view.Expanded };
            inspector.SetResult(result, evidenceEntity);
            content.Children.Add(inspector);
            Content = content;
            return;
        }
        if (ShowSummary)
        {
            content.Children.Add(Summary(result));
        }

        if (result.Document.Roots.Count == 0)
        {
            content.Children.Add(Muted("The query completed successfully but found no matching entities."));
        }

        Content = content;
        IReadOnlyList<EntityProjectionNode> entities = _view?.Entity is { } selected ? [selected] : result.Document.Roots;
        int index = 0;
        bool queued = false;
        // Controls must be built on the UI thread, but a whole page need not occupy
        // one dispatcher turn. Yield between bounded batches for layout and input.
        void Schedule()
        {
            if (queued || version != _renderVersion || VisualRoot is null)
            {
                return;
            }

            queued = true;
            SetRendering(true);
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                queued = false;
                if (version != _renderVersion || VisualRoot is null)
                {
                    return;
                }

                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                int count = 0;
                while (index < entities.Count && count++ < 4)
                {
                    content.Children.Add(EntityCard(entities[index++], evidence));
                    if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 8)
                    {
                        break;
                    }
                }
                if (index < entities.Count)
                {
                    Schedule();
                }
                else
                {
                    content.Children.Add(RawResult(result));
                    _resumeRendering = null;
                    SetRendering(false);
                }
            }, global::Avalonia.Threading.DispatcherPriority.Background);
        }
        _resumeRendering = Schedule;
        Schedule();
    }

    private static Control Summary(EntityProjectionQueryResult result)
    {
        string count = result.Document.Roots.Count == 1 ? "1 entity" : $"{result.Document.Roots.Count:N0} entities";
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                new TextBlock { Text = count, Classes = { "ef-section-title" } },
                At(Muted($"Snapshot {result.SnapshotVersion.Value:N0}"), 1),
            },
        };
    }

    private Control EntityCard(EntityProjectionNode entity, IReadOnlyDictionary<Uri, EvidenceRecord> evidence)
    {
        string title = PreferredTitle(entity);
        string types = entity.Types.Count == 0 ? "Entity" : String.Join(" · ", entity.Types.Select(item => Friendly(item.Identifier)));
        var body = new StackPanel { Spacing = 0 };
        foreach (EntityProjectionField field in entity.Fields)
        {
            body.Children.Add(Field(field, evidence, Identity(entity.Identity)));
        }

        foreach (EntityProjectionRelationship relationship in entity.Relationships)
        {
            body.Children.Add(Relationship(relationship));
        }

        if (entity.Fields.Count == 0 && entity.Relationships.Count == 0)
        {
            body.Children.Add(Muted("No projected fields were returned for this entity."));
        }

        var advanced = new Expander { Header = "Advanced entity information" };
        RememberExpansion(advanced, _view?.Expanded, "entity:" + Identity(entity.Identity));
        advanced.Content = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                TechnicalRow("Identity", Identity(entity.Identity)),
                TechnicalRow("Concepts", entity.Types.Count == 0 ? "None" :
                    String.Join(Environment.NewLine, entity.Types.Select(item => item.Identifier.AbsoluteUri))),
            },
        };
        body.Children.Add(advanced);
        var inspect = new Button { Content = "Inspect Evidence", HorizontalAlignment = HorizontalAlignment.Left };
        inspect.Click += (_, _) =>
        {
            Exploration?.OpenEvidence(_page!, _view?.Result ?? _page!, entity);
            InspectEvidenceRequested?.Invoke(entity);
            EvidenceRequested?.Invoke(_view?.Result ?? _page!, entity);
            NavigationChanged?.Invoke();
        };
        inspect.Margin = new Thickness(0, 6, 0, 8);
        body.Children.Insert(0, inspect);
        if (Explorer is not null && entity.Types.Count > 0)
        {
            var explore = new Button { Content = "Explore Entity", HorizontalAlignment = HorizontalAlignment.Left };
            var error = Muted(String.Empty);
            explore.Click += async (_, _) =>
            {
                explore.IsEnabled = false;
                error.Text = "Loading entity…";
                try
                {
                    var result = await Explorer.InspectAsync(Identity(entity.Identity), entity.Types[0].Identifier.AbsoluteUri,
                        _page!.SnapshotVersion.Value);
                    if (IsCurrent?.Invoke() == false)
                    {
                        return;
                    }

                    if (result.SnapshotVersion != _page.SnapshotVersion)
                    {
                        throw new InvalidDataException("The entity belongs to a different snapshot. Run the query again.");
                    }

                    var found = result.Document.Roots.FirstOrDefault();
                    if (found is null)
                    {
                        error.Text = "This entity is unavailable in the result snapshot.";
                        return;
                    }
                    Exploration?.Open(_page, result, found);
                    NavigationChanged?.Invoke();
                }
                catch (Exception exception)
                {
                    error.Text = $"Could not explore this entity. {exception.Message}";
                }
                finally
                {
                    explore.IsEnabled = true;
                }
            };
            body.Children.Add(explore);
            body.Children.Add(error);
        }
        var card = new Border
        {
            Padding = new Thickness(14),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 2, Children =
                    {
                        new TextBlock { Text = title, Classes = { "ef-entity-title" } },
                        Muted(types),
                    }
                    },
                    body,
                },
            },
        };
        card.Classes.Add("ef-list-row");
        return card;
    }

    private Control Field(EntityProjectionField field, IReadOnlyDictionary<Uri, EvidenceRecord> evidence, string identity)
    {
        string values = field.Values.Count == 0 ? "—" : String.Join(", ", field.Values.Select(DisplayValue));
        int evidenceCount = field.Values.SelectMany(item => item.Evidence).Select(item => item.Identifier).Distinct().Count();
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("140,*,Auto,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(0, 5)
        };
        row.Children.Add(new TextBlock
        {
            Text = Friendly(field.Name),
            Classes = { "ef-field-label" },
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(At(new TextBlock
        {
            Text = values,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        }, 1));
        var evidenceButton = new Button
        {
            Content = $"Evidence: {evidenceCount}",
            Padding = new Thickness(6, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        global::Avalonia.Automation.AutomationProperties.SetName(evidenceButton, $"Show evidence for {Friendly(field.Name)}");
        if (evidenceCount > 0)
        {
            row.Children.Add(At(evidenceButton, 2));
        }

        var details = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 8), IsVisible = false };
        string key = identity + "\n" + field.Name;
        details.IsVisible = _view?.Expanded.Contains(key) == true;
        bool detailsBuilt = false;
        void EnsureDetails()
        {
            if (detailsBuilt)
            {
                return;
            }

            detailsBuilt = true;
            foreach (EntityProjectionValue value in field.Values)
            {
                details.Children.Add(TechnicalRow("Value", DisplayValue(value)));
                details.Children.Add(TechnicalRow("Value type", ValueType(value.Value)));
                details.Children.Add(TechnicalRow("Projected field", value.ProjectedTerm.Identifier.AbsoluteUri));
                if (value.SourceTerm != value.ProjectedTerm)
                {
                    details.Children.Add(TechnicalRow("Original source field", value.SourceTerm.Identifier.AbsoluteUri));
                }

                foreach (EvidenceReference reference in value.Evidence)
                {
                    details.Children.Add(evidence.TryGetValue(reference.Identifier, out EvidenceRecord? record)
                        ? EvidenceCard(record, compact: true)
                        : UnknownEvidence(reference));
                }
            }
            if (field.Values.All(item => item.Evidence.Count == 0))
            {
                details.Children.Add(Muted("No evidence references were returned for this field."));
            }
        }
        if (details.IsVisible)
        {
            EnsureDetails();
        }

        var closeGeometry = Geometry.Parse("M 1,0 L 6,5 11,0 12,1 7,6 12,11 11,12 6,7 1,12 0,11 5,6 0,1 Z");
        var moreGeometry = Geometry.Parse("M 0,6 A 1,1 0 1 0 2,6 A 1,1 0 1 0 0,6 Z M 5,6 A 1,1 0 1 0 7,6 A 1,1 0 1 0 5,6 Z M 10,6 A 1,1 0 1 0 12,6 A 1,1 0 1 0 10,6 Z");
        var detailIcon = new PathIcon { Width = 12, Height = 12, Data = details.IsVisible ? closeGeometry : moreGeometry };
        var disclose = new Button
        {
            Content = detailIcon,
            Padding = new Thickness(7, 4),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        disclose.Classes.Add("ef-command");
        ToolTip.SetTip(disclose, details.IsVisible ? "Hide field details" : "Show field definition and evidence");
        void ToggleDetails()
        {
            details.IsVisible = !details.IsVisible;
            if (details.IsVisible)
            {
                EnsureDetails();
            }

            if (details.IsVisible)
            {
                _view?.Expanded.Add(key);
            }
            else
            {
                _view?.Expanded.Remove(key);
            }

            detailIcon.Data = details.IsVisible ? closeGeometry : moreGeometry;
            ToolTip.SetTip(disclose, details.IsVisible ? "Hide field details" : "Show field definition and evidence");
        }
        disclose.Click += (_, _) => ToggleDetails();
        evidenceButton.Click += (_, _) => ToggleDetails();
        global::Avalonia.Automation.AutomationProperties.SetName(disclose, $"Details for {Friendly(field.Name)}");
        row.Children.Add(At(disclose, 3));
        bool? narrowLayout = null;
        row.SizeChanged += (_, _) =>
        {
            bool narrow = row.Bounds.Width < 520;
            if (narrowLayout == narrow)
            {
                return;
            }

            narrowLayout = narrow;
            row.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,Auto,Auto" : "140,*,Auto,Auto");
            row.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
            var value = row.Children[1];
            Grid.SetColumn(value, narrow ? 0 : 1);
            Grid.SetRow(value, narrow ? 1 : 0);
            Grid.SetColumnSpan(value, narrow ? 3 : 1);
            Grid.SetColumn(evidenceButton, narrow ? 1 : 2);
            Grid.SetColumn(disclose, narrow ? 2 : 3);
        };
        var container = new Border { Child = new StackPanel { Spacing = 0, Children = { row, details } } };
        container.Classes.Add("ef-entity-field-row");
        return container;
    }

    private Control Relationship(EntityProjectionRelationship relationship)
    {
        string detail = relationship.Items.Count == 1 ? "1 related entity" : $"{relationship.Items.Count:N0} related entities";
        var row = new Border
        {
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("140,*"),
                ColumnSpacing = 8,
                Margin = new Thickness(0, 7),
                Children =
            {
                new TextBlock { Text = Friendly(relationship.Name), FontWeight = FontWeight.SemiBold },
                At(new TextBlock { Text = detail }, 1),
            },
            }
        };
        row.Classes.Add("ef-entity-field-row");
        var items = new StackPanel { Spacing = 8 };
        items.Children.Add(row);
        foreach (var item in relationship.Items)
        {
            if (item is EntityProjectionNode node)
            {
                var open = new Button { Content = PreferredTitle(node), HorizontalAlignment = HorizontalAlignment.Left };
                open.Click += (_, _) =>
                {
                    Exploration?.Open(_page!, _view?.Result ?? _page!, node);
                    NavigationChanged?.Invoke();
                };
                items.Children.Add(open);
            }
            else if (item is EntityProjectionReference reference)
            {
                items.Children.Add(Muted(Identity(reference.Identity)));
            }
        }
        return items;
    }

    private static Control RawResult(EntityProjectionQueryResult result)
    {
        var raw = new Expander { Header = "Advanced query diagnostics" };
        raw.PropertyChanged += (_, args) =>
        {
            if (args.Property != Expander.IsExpandedProperty || !raw.IsExpanded || raw.Content is not null)
            {
                return;
            }

            raw.Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Muted("Canonical transport JSON is intended for diagnostics and integration troubleshooting."),
                    new TextBox
                    {
                        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
                        MinHeight = 180, Text = JsonSerializer.Serialize(EntityProjectionTransport.Encode(result),
                            ProjectionDiagnosticsJsonContext.Default.EntityProjectionTransportResponse)
                    },
                },
            };
        };
        return raw;
    }

    internal static Control EvidenceCard(EvidenceRecord record, bool compact, HashSet<string>? expanded = null)
    {
        string provider = Friendly(record.ProviderIdentifier.AbsoluteUri);
        string source = Source(record);
        var details = new StackPanel { Spacing = 4 };
        if (record.ObservedAt is not null)
        {
            details.Children.Add(TechnicalRow("Observed", DisplayTime(record.ObservedAt.Value)));
        }

        if (record.ValidFrom is not null || record.ValidTo is not null)
        {
            details.Children.Add(TechnicalRow("Valid", $"{DisplayTime(record.ValidFrom)} — {DisplayTime(record.ValidTo)}"));
        }

        if (!String.IsNullOrWhiteSpace(record.SnapshotIdentifier))
        {
            details.Children.Add(TechnicalRow("Source snapshot", record.SnapshotIdentifier));
        }

        if (record.SourceGraph is not null)
        {
            details.Children.Add(TechnicalRow("Source collection", Friendly(record.SourceGraph)));
        }

        details.Children.Add(TechnicalRow("Evidence identifier", record.Reference.Identifier.AbsoluteUri));
        details.Children.Add(TechnicalRow("Provider identifier", record.ProviderIdentifier.AbsoluteUri));
        var evidenceDetails = new Expander { Header = "Evidence details", Content = details };
        RememberExpansion(evidenceDetails, expanded, "record:" + record.Reference.Identifier.AbsoluteUri);
        var card = new Border
        {
            Padding = compact ? new Thickness(10) : new Thickness(12),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    TechnicalRow("Provider", provider),
                    TechnicalRow("Source", source),
                    evidenceDetails,
                },
            },
        };
        card.Classes.Add("ef-list-row");
        return card;
    }

    internal static void RememberExpansion(Expander expander, HashSet<string>? expanded, string key)
    {
        expander.IsExpanded = expanded?.Contains(key) == true;
        expander.PropertyChanged += (_, args) =>
        {
            if (args.Property != Expander.IsExpandedProperty || expanded is null)
            {
                return;
            }

            if (expander.IsExpanded)
            {
                expanded.Add(key);
            }
            else
            {
                expanded.Remove(key);
            }
        };
    }

    internal static string Friendly(Uri value) => Friendly(value.AbsoluteUri);

    internal static string Source(EvidenceRecord record) => record.SourceRecordIdentifier ??
        (record.SourceGraph is null ? "Not identified" : Friendly(record.SourceGraph));

    internal static string Friendly(string value)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            return "Unnamed";
        }

        try
        {
            return QueryDisplayName.FromIdentifier(value);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    private static Control UnknownEvidence(EvidenceReference reference) => new Border
    {
        Padding = new Thickness(10),
        Child = new StackPanel
        {
            Spacing = 3,
            Children =
        {
            new TextBlock { Text = Friendly(reference.Identifier), FontWeight = FontWeight.SemiBold },
            Muted("This result referenced evidence whose descriptive record was not included."),
            new Expander { Header = "Evidence identifier", Content = TechnicalRow("URI", reference.Identifier.AbsoluteUri) },
        }
        },
    };

    public static string PreferredTitle(EntityProjectionNode entity)
    {
        // Projection aliases are presentation choices, not the identity of the underlying property.
        // Prefer a complete readable name, and keep the stable entity ID in advanced information.
        foreach (string preferred in new[] { "displayname", "fullname", "name", "label", "title" })
        {
            foreach (EntityProjectionField field in entity.Fields)
            {
                foreach (EntityProjectionValue value in field.Values)
                {
                    if (value.Value is LiteralValue literal && !String.IsNullOrWhiteSpace(literal.LexicalValue) &&
                        (TitleKey(field.Name) == preferred ||
                         TitleKey(Friendly(value.ProjectedTerm.Identifier)) == preferred ||
                         TitleKey(Friendly(value.SourceTerm.Identifier)) == preferred))
                    {
                        return literal.LexicalValue;
                    }
                }
            }
        }

        return Friendly(Identity(entity.Identity));
    }

    private static string TitleKey(string name) => name.Replace(" ", "", StringComparison.Ordinal)
        .Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

    private static string DisplayValue(EntityProjectionValue value) => value.Value switch
    {
        LiteralValue literal when literal.ScalarKind == QueryScalarKind.Boolean && Boolean.TryParse(literal.LexicalValue, out bool boolean) => boolean ? "Yes" : "No",
        LiteralValue literal when literal.ScalarKind == QueryScalarKind.Integer && Int64.TryParse(literal.LexicalValue,
            NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer) => integer.ToString("N0", CultureInfo.CurrentCulture),
        LiteralValue literal when literal.ScalarKind == QueryScalarKind.Decimal && Decimal.TryParse(literal.LexicalValue,
            NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number) => number.ToString("N", CultureInfo.CurrentCulture),
        LiteralValue literal => literal.LexicalValue,
        IdentifierValue identifier => Friendly(identifier.Identifier),
        BlankNodeValue blank => blank.Identifier,
        JsonValue json => json.Value.Value,
        NullValue => "None",
        UnboundValue => "Not available",
        _ => value.Value.ToString() ?? String.Empty,
    };

    private static string ValueType(QueryValue value) => value switch
    {
        LiteralValue literal => literal.ScalarKind.ToString(),
        IdentifierValue => "Identifier",
        BlankNodeValue => "Blank node",
        JsonValue => "Structured JSON",
        _ => value.GetType().Name,
    };

    private static string Identity(GraphElementIdentity identity) => identity switch
    {
        IdentifierValue value => value.Identifier,
        BlankNodeValue value => value.Identifier,
        _ => identity.ToString() ?? String.Empty,
    };

    private static string DisplayTime(DateTimeOffset? value) => value is null ? "Open" : DisplayTime(value.Value);

    private static string DisplayTime(DateTimeOffset value) => value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private static TextBlock Muted(string text)
    {
        var result = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        result.Classes.Add("ef-muted");
        return result;
    }

    private static TextBlock Badge(string text)
    {
        var result = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        result.Classes.Add("ef-evidence");
        return result;
    }

    private static Control TechnicalRow(string label, string value) => new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("140,*"),
        ColumnSpacing = 8,
        Children = { Muted(label), At(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap }, 1) },
    };

    private static T At<T>(T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }
}

/// <summary>Human-oriented evidence overview for an EntityProjection result.</summary>
public sealed class EntityProjectionEvidenceControl : ContentControl
{
    public HashSet<string>? Expanded
    {
        get; set;
    }

    public void SetResult(EntityProjectionQueryResult result, EntityProjectionNode? selected = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (selected is not null)
        {
            var identifiers = Values(selected).SelectMany(value => value.Evidence)
                .Select(reference => reference.Identifier.AbsoluteUri).ToHashSet(StringComparer.Ordinal);
            result = new EntityProjectionQueryResult(
                new EntityProjection(result.Document.SourceQueryId, [selected],
                    result.Document.Evidence.Where(record => identifiers.Contains(record.Reference.Identifier.AbsoluteUri)).ToArray()),
                result.SnapshotVersion);
        }
        var content = new StackPanel { Spacing = 10 };
        EntityProjectionNode[] supportedEntities = result.Document.Roots.Where(item => Values(item)
            .Any(value => value.Evidence.Count != 0)).ToArray();
        int supportedValues = result.Document.Roots.SelectMany(Values).Count(item => item.Evidence.Count != 0);
        int providers = result.Document.Evidence.Select(item => item.ProviderIdentifier).Distinct().Count();
        content.Children.Add(new TextBlock
        {
            Text = $"{supportedEntities.Length:N0} supported " +
            $"{(supportedEntities.Length == 1 ? "entity" : "entities")}",
            Classes = { "ef-section-title" }
        });
        content.Children.Add(Muted($"{supportedValues:N0} returned field {(supportedValues == 1 ? "value is" : "values are")} linked " +
            $"to {result.Document.Evidence.Count:N0} evidence {(result.Document.Evidence.Count == 1 ? "record" : "records")} " +
            $"from {providers:N0} {(providers == 1 ? "provider" : "providers")}."));
        content.Children.Add(Muted("A provider is the process or integration that supplied an assertion. " +
            "A source is the original system, document or record from which it came."));
        if (result.Document.Evidence.Count == 0)
        {
            content.Children.Add(Muted("The query returned no evidence records. Ensure ‘Include evidence’ is selected if provenance is required."));
        }

        foreach (IGrouping<Uri, EvidenceRecord> provider in result.Document.Evidence.GroupBy(item => item.ProviderIdentifier))
        {
            var providerBody = new StackPanel { Spacing = 8 };
            providerBody.Children.Add(Muted("This provider supplied the following original sources or records."));
            foreach (IGrouping<string, EvidenceRecord> source in provider.GroupBy(EntityProjectionResultsControl.Source))
            {
                var sourceBody = new StackPanel { Spacing = 6 };
                foreach (EvidenceRecord record in source)
                {
                    sourceBody.Children.Add(EntityProjectionResultsControl.EvidenceCard(record, compact: false, Expanded));
                }

                var sourceExpander = new Expander
                {
                    Header = $"Source: {source.Key} · {source.Count()} {(source.Count() == 1 ? "record" : "records")}",
                    Content = sourceBody,
                };
                EntityProjectionResultsControl.RememberExpansion(sourceExpander, Expanded, "source:" + provider.Key + "\n" + source.Key);
                providerBody.Children.Add(sourceExpander);
            }
            var providerExpander = new Expander
            {
                Header = $"Provider: {EntityProjectionResultsControl.Friendly(provider.Key)} · " +
                         $"{provider.Count()} evidence {(provider.Count() == 1 ? "record" : "records")}",
                Content = providerBody,
            };
            EntityProjectionResultsControl.RememberExpansion(providerExpander, Expanded, "provider:" + provider.Key);
            content.Children.Add(providerExpander);
        }
        Content = content;
    }

    private static IEnumerable<EntityProjectionValue> Values(EntityProjectionNode node) =>
        node.Fields.SelectMany(item => item.Values).Concat(node.Relationships.SelectMany(item => item.Items)
            .OfType<EntityProjectionNode>().SelectMany(Values));

    private static TextBlock Muted(string text)
    {
        var result = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        result.Classes.Add("ef-muted");
        return result;
    }
}
