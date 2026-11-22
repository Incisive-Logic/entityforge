using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using EntityForge.Db.AppModel.Queries;
using EntityForge.UI.Avalonia.Controls;

namespace EntityForge.Db.AppModel.Avalonia.Queries;

public sealed class SemanticQueryRequestedEventArgs(SemanticQueryDocument query) : EventArgs
{
    public SemanticQueryDocument Query { get; } = query;
}

/// <summary>A reusable, catalogue-driven Semantic query-block editor.</summary>
public sealed class SemanticQueryBuilderControl : ContentControl
{
    private const int MaximumEditorDepth = 4;
    private readonly HashSet<string> _projection = new(StringComparer.Ordinal);
    private SemanticQueryMetadata? _metadata;
    private QueryGroupState? _root;
    private SemanticQueryDocument? _preservedQuery;
    private readonly Dictionary<string, string> _projectionNames = new(StringComparer.Ordinal);
    private readonly List<SemanticQueryOrdering> _orderings = [];
    private Button? _run;
    private TextBlock? _validation;
    private int _first = 50;
    private bool _includeEvidence = true;
    private QueryConceptMatchMode _conceptMatchMode = QueryConceptMatchMode.IncludeNarrowerConcepts;

    public event EventHandler<SemanticQueryRequestedEventArgs>? QueryRequested;

    public event EventHandler<SemanticQueryRequestedEventArgs>? DraftChanged;

    public SemanticQueryDocument? CurrentQuery => _preservedQuery ?? (_root is null ? null : Build());

    public bool IsRunning
    {
        get; set;
    }

    public bool ShowTechnicalDetails
    {
        get; set;
    }

    /// <summary>Optional host for execution and validation controls outside the scrolling editor.</summary>
    public ContentControl? ExecutionHost
    {
        get; set;
    }

    public void SetQuery(SemanticQueryMetadata metadata, SemanticQueryDocument? query = null)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _preservedQuery = query is not null && !SemanticQueryBuilderSupport.CanEdit(metadata, query) ? query : null;
        _projectionNames.Clear();
        foreach (var field in query?.Projection ?? [])
        {
            _projectionNames.TryAdd(field.Property.Identifier, field.ResultName);
        }

        _orderings.Clear();
        _orderings.AddRange(query?.OrderBy ?? []);
        string? concept = query?.PrimaryConcept.Identifier ?? metadata.Concepts.FirstOrDefault()?.Identifier;
        _root = concept is null || _preservedQuery is not null ? null : Hydrate(concept, query?.Conditions ?? []);
        _first = query?.First ?? 50;
        _includeEvidence = query?.IncludeEvidence ?? true;
        _conceptMatchMode = query?.ConceptMatchMode ?? QueryConceptMatchMode.IncludeNarrowerConcepts;
        _projection.Clear();
        foreach (SemanticQueryProjectionField field in query?.Projection ?? [])
        {
            _projection.Add(field.Property.Identifier);
        }

        if (query is null && concept is not null)
        {
            foreach (SemanticQueryProperty property in Properties(concept))
            {
                _projection.Add(property.Identifier);
            }
        }

        Rebuild();
    }

    private QueryGroupState Hydrate(string concept, IReadOnlyList<SemanticQueryBlock> blocks)
    {
        var group = new QueryGroupState(concept);
        foreach (SemanticQueryBlock block in blocks)
        {
            Hydrate(group, block);
        }

        return group;
    }

    private void Hydrate(QueryGroupState group, SemanticQueryBlock block)
    {
        switch (block)
        {
            case SemanticPropertyConditionBlock property:
                group.Conditions.Add(new(property.Property.Identifier, property.Operator,
                    property.Operator == QueryComparisonOperator.In ? String.Join("\n", property.Value.EnumerateArray().Select(DisplayValue)) : DisplayValue(property.Value)));
                break;
            case SemanticRelationshipConditionBlock relationship:
                group.Relationships.Add(new(relationship.Relationship.Identifier,
                    Hydrate(relationship.TargetConcept.Identifier, relationship.Conditions))
                {
                    Negated = relationship.Negated
                });
                group.Relationships[^1].Target.Operator = relationship.Operator;
                break;
            case SemanticLogicalQueryBlock logical:
                var nested = Hydrate(group.Concept, logical.Children);
                nested.Operator = logical.Operator;
                group.Groups.Add(nested);
                break;
        }
    }

    private void Rebuild()
    {
        if (ExecutionHost is not null)
        {
            ExecutionHost.Content = null;
        }

        if (_preservedQuery is { } preserved)
        {
            var runPreserved = new Button
            {
                Content = IsRunning ? "Running…" : "Run Query",
                IsEnabled = !IsRunning,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            runPreserved.Classes.Add("ef-primary");
            runPreserved.Click += (_, _) => QueryRequested?.Invoke(this, new(preserved));
            var preservedBody = new StackPanel
            {
                Spacing = 14,
                Children =
            {
                Muted("This query uses features the builder cannot edit. Its original meaning is preserved."),
                new Expander
                {
                    Header = "Inspect query", Content = new TextBox
                {
                    IsReadOnly = true, AcceptsReturn = true,
                    Text = JsonSerializer.Serialize(preserved, BuilderJsonContext.Default.SemanticQueryDocument)
                }
                },
            }
            };
            if (ExecutionHost is not null)
            {
                ExecutionHost.Content = runPreserved;
            }
            else
            {
                preservedBody.Children.Add(runPreserved);
            }

            Content = preservedBody;
            return;
        }
        if (_metadata is null || _metadata.Concepts.Count == 0 || _root is null)
        {
            Content = Muted("This Semantic host has not published any queryable concepts.");
            return;
        }

        var content = new StackPanel { Spacing = 14 };
        var find = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 6 };
        var findHeading = Label("Find");
        findHeading.VerticalAlignment = VerticalAlignment.Center;
        find.Children.Add(findHeading);
        ComboBox concepts = Selection(_metadata.Concepts.Select(item => (item.Identifier, item.DisplayName)), _root.Concept);
        concepts.HorizontalAlignment = HorizontalAlignment.Left;
        concepts.MinWidth = 0;
        concepts.AttachedToVisualTree += (_, _) =>
        {
            double longest = 0;
            foreach (var concept in _metadata.Concepts)
            {
                var label = new TextBlock { Text = concept.DisplayName, FontFamily = concepts.FontFamily, FontSize = concepts.FontSize, FontWeight = concepts.FontWeight };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                longest = Math.Max(longest, label.DesiredSize.Width);
            }
            concepts.Width = Math.Ceiling(longest) + 56; // Text padding and dropdown button.
        };
        find.SizeChanged += (_, args) => concepts.MaxWidth = args.NewSize.Width;
        var includeNarrower = new CheckBox
        {
            Content = "Include Narrower Concepts",
            IsChecked = _conceptMatchMode == QueryConceptMatchMode.IncludeNarrowerConcepts,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(includeNarrower,
            "Also match more specific concepts in the model. Uncheck to match only the selected concept.");
        concepts.SelectionChanged += (_, _) =>
        {
            if (SelectedKey(concepts) is not { } identifier)
            {
                return;
            }

            _root = new QueryGroupState(identifier);
            _orderings.Clear();
            _projectionNames.Clear();
            _projection.Clear();
            foreach (SemanticQueryProperty property in Properties(identifier))
            {
                _projection.Add(property.Identifier);
            }

            Rebuild();
        };
        includeNarrower.IsCheckedChanged += (_, _) =>
        {
            _conceptMatchMode = includeNarrower.IsChecked == true
                ? QueryConceptMatchMode.IncludeNarrowerConcepts : QueryConceptMatchMode.ExactConceptOnly;
            NotifyDraftChanged();
        };
        find.Children.Add(concepts);
        find.Children.Add(includeNarrower);
        content.Children.Add(find);

        content.Children.Add(GroupEditor(_root, 0, true));

        var returns = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 6 };
        var returnHeading = Label("Return");
        returnHeading.VerticalAlignment = VerticalAlignment.Center;
        returns.Children.Add(returnHeading);
        var fields = new ChecklistPicker
        {
            AllSelectedText = "All Properties",
            NoneSelectedText = "Identity Only",
            ItemLabel = "Property",
            ItemsLabel = "Properties",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        fields.SetOptions(Properties(_root.Concept).Select(p => new ChecklistOption(p.Identifier, p.DisplayName)), _projection);
        fields.SelectionChanged += (_, _) =>
        {
            _projection.Clear();
            _projection.UnionWith(fields.SelectedKeys);
            NotifyDraftChanged();
        };
        returns.Children.Add(fields);
        content.Children.Add(returns);
        content.Children.Add(SortEditor());

        var options = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 6 };
        var first = new NumericUpDown { Minimum = 1, Maximum = 10_000, Value = _first, Width = 168 };
        var evidence = new CheckBox
        {
            Content = "Include evidence",
            IsChecked = _includeEvidence,
            VerticalAlignment = VerticalAlignment.Center
        };
        first.ValueChanged += (_, _) =>
        {
            _first = Decimal.ToInt32(first.Value ?? 50);
            NotifyDraftChanged();
        };
        evidence.IsCheckedChanged += (_, _) =>
        {
            _includeEvidence = evidence.IsChecked == true;
            NotifyDraftChanged();
        };
        options.Children.Add(Label("Page Size"));
        options.Children.Add(first);
        options.Children.Add(evidence);
        content.Children.Add(options);

        var queryShape = new Border { Child = content };
        queryShape.Classes.Add("ef-control-group");
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(queryShape);

        var run = new Button
        {
            Content = IsRunning ? "Running…" : "Run Query",
            IsEnabled = !IsRunning,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        run.Classes.Add("ef-primary");
        run.Click += (_, _) =>
        {
            var query = Build();
            if (SemanticQueryBuilderSupport.CanEdit(_metadata, query))
            {
                QueryRequested?.Invoke(this, new(query));
            }
        };
        _run = run;
        _validation = Muted(String.Empty);
        var actions = new StackPanel { Spacing = 6, Children = { _validation, run } };
        if (ExecutionHost is not null)
        {
            ExecutionHost.Content = actions;
        }
        else
        {
            body.Children.Add(actions);
        }

        if (ShowTechnicalDetails)
        {
            var inspect = new Expander { Header = "Inspect canonical query document" };
            inspect.Content = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                MinHeight = 100,
                Text = JsonSerializer.Serialize(Build(), BuilderJsonContext.Default.SemanticQueryDocument)
            };
            body.Children.Add(inspect);
        }
        Content = body;
        NotifyDraftChanged();
    }

    private Control SortEditor()
    {
        var body = new StackPanel { Spacing = 8 };
        for (int index = 0; index < _orderings.Count; index++)
        {
            int slot = index;
            var order = _orderings[slot];
            var property = Selection(Properties(_root!.Concept).Select(p => (p.Identifier, p.DisplayName)), order.Property.Identifier);
            var direction = Selection(new[] { ("Ascending", "Ascending"), ("Descending", "Descending") }, order.Direction.ToString());
            var missing = Selection(new[] { ("First", "Missing first"), ("Last", "Missing last") }, order.MissingValues.ToString());
            var values = Selection(new[] { ("Minimum", "Lowest value"), ("Maximum", "Highest value") }, order.ValueSelection.ToString());
            ToolTip.SetTip(values, "When a property has several values, choose which one determines its order.");
            property.SelectionChanged += (_, _) =>
            {
                if (SelectedKey(property) is { } key)
                {
                    _orderings[slot] = _orderings[slot] with
                    {
                        Property = Properties(_root.Concept).Single(p => p.Identifier == key)
                    };
                }
                NotifyDraftChanged();
            };
            direction.SelectionChanged += (_, _) =>
            {
                if (Enum.TryParse(SelectedKey(direction), out QuerySortDirection value))
                {
                    _orderings[slot] = _orderings[slot] with
                    {
                        Direction = value
                    };
                }
                NotifyDraftChanged();
            };
            missing.SelectionChanged += (_, _) =>
            {
                if (Enum.TryParse(SelectedKey(missing), out QueryMissingValuePlacement value))
                {
                    _orderings[slot] = _orderings[slot] with
                    {
                        MissingValues = value
                    };
                }
                NotifyDraftChanged();
            };
            values.SelectionChanged += (_, _) =>
            {
                if (Enum.TryParse(SelectedKey(values), out QuerySortValueSelection value))
                {
                    _orderings[slot] = _orderings[slot] with
                    {
                        ValueSelection = value
                    };
                }
                NotifyDraftChanged();
            };
            var up = Command("↑");
            up.IsEnabled = slot > 0;
            ToolTip.SetTip(up, "Move Up — Higher Sort Priority");
            global::Avalonia.Automation.AutomationProperties.SetName(up, "Move Sort Field Up");
            up.Click += (_, _) =>
            {
                (_orderings[slot - 1], _orderings[slot]) = (_orderings[slot], _orderings[slot - 1]);
                Rebuild();
            };
            var down = Command("↓");
            down.IsEnabled = slot < _orderings.Count - 1;
            ToolTip.SetTip(down, "Move Down — Lower Sort Priority");
            global::Avalonia.Automation.AutomationProperties.SetName(down, "Move Sort Field Down");
            down.Click += (_, _) =>
            {
                (_orderings[slot + 1], _orderings[slot]) = (_orderings[slot], _orderings[slot + 1]);
                Rebuild();
            };
            var advanced = Command("Advanced");
            advanced.Flyout = new Flyout
            {
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Label("Entities Without This Property"), missing,
                        Label("When a Property Has Multiple Values"), values
                    }
                }
            };
            var remove = Command("×");
            ToolTip.SetTip(remove, "Remove Sort Field");
            global::Avalonia.Automation.AutomationProperties.SetName(remove, "Remove Sort Field");
            remove.Click += (_, _) =>
            {
                _orderings.RemoveAt(slot);
                Rebuild();
            };
            body.Children.Add(new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { property, direction, up, down, advanced, remove } });
        }
        var add = Command("+ Add Sort Field");
        add.IsEnabled = Properties(_root!.Concept).Count > 0;
        add.Click += (_, _) =>
        {
            _orderings.Add(new(Properties(_root.Concept)[0]));
            Rebuild();
        };
        body.Children.Add(add);
        ToolTip.SetTip(add, "Additional fields break ties in earlier fields. Entity identity breaks any remaining ties for stable paging.");
        var heading = Label("Sort by");
        heading.VerticalAlignment = VerticalAlignment.Top;
        heading.Margin = new Thickness(0, 8, 12, 0);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children = { heading, At(body, 1) }
        };
    }

    private Control GroupEditor(QueryGroupState group, int depth, bool root)
    {
        var content = new StackPanel { Spacing = 8 };
        var commands = new WrapPanel { Orientation = Orientation.Horizontal };
        if (root)
        {
            var heading = Label("Where");
            heading.VerticalAlignment = VerticalAlignment.Center;
            heading.Margin = new Thickness(0, 0, 12, 0);
            commands.Children.Add(heading);
        }
        if (!root)
        {
            var logic = Selection(new[] { ("All", "All conditions (AND)"), ("Any", "Any condition (OR)"), ("None", "None of these (NOT OR)") }, group.Operator.ToString());
            logic.SelectionChanged += (_, _) =>
            {
                if (Enum.TryParse(SelectedKey(logic), out QueryLogicalOperator op))
                {
                    group.Operator = op;
                }
                NotifyDraftChanged();
            };
            content.Children.Add(logic);
        }
        var addGroup = Command("+ Add Group");
        addGroup.IsEnabled = depth < MaximumEditorDepth;
        addGroup.Click += (_, _) =>
        {
            group.Groups.Add(new(group.Concept));
            Rebuild();
        };
        commands.Children.Add(addGroup);
        var addCondition = Command("+ Add Condition");
        addCondition.Margin = new Thickness(0, 0, 8, 0);
        addCondition.IsEnabled = Properties(group.Concept).Count != 0;
        addCondition.Click += (_, _) =>
        {
            SemanticQueryProperty property = Properties(group.Concept)[0];
            group.Conditions.Add(new(property.Identifier, property.Operators.FirstOrDefault(), String.Empty));
            Rebuild();
        };
        commands.Children.Add(addCondition);
        var addRelationship = Command("+ Add Relationship");
        addRelationship.IsEnabled = depth < MaximumEditorDepth && Relationships(group.Concept).Count != 0;
        addRelationship.Click += (_, _) =>
        {
            SemanticQueryRelationship relationship = Relationships(group.Concept)[0];
            group.Relationships.Add(new(relationship.Identifier, new QueryGroupState(DefaultTarget(relationship))));
            Rebuild();
        };
        commands.Children.Add(addRelationship);
        if (root)
        {
            content.Children.Add(commands);
        }

        for (int i = 0; i < group.Conditions.Count; i++)
        {
            content.Children.Add(Condition(group, i));
        }

        for (int i = 0; i < group.Relationships.Count; i++)
        {
            content.Children.Add(Relationship(group, i, depth));
        }

        foreach (var nested in group.Groups)
        {
            var removeGroup = Command("Remove Group");
            removeGroup.Click += (_, _) =>
            {
                group.Groups.Remove(nested);
                Rebuild();
            };
            var groupBody = new StackPanel { Spacing = 8, Children = { GroupEditor(nested, depth + 1, false), removeGroup } };
            var border = new Border { Child = groupBody, Padding = new Thickness(12), Margin = new Thickness(12, 4, 0, 4) };
            border.Classes.Add("ef-control-group");
            content.Children.Add(border);
        }
        if (!root)
        {
            content.Children.Add(commands);
        }

        return content;
    }

    private Control Condition(QueryGroupState group, int index)
    {
        ConditionState state = group.Conditions[index];
        IReadOnlyList<SemanticQueryProperty> properties = Properties(group.Concept);
        SemanticQueryProperty selected = properties.FirstOrDefault(item => item.Identifier == state.Property) ?? properties[0];
        ComboBox property = Selection(properties.Select(item => (item.Identifier, item.DisplayName)), selected.Identifier);
        ComboBox comparison = Selection(selected.Operators.Select(item => (item.ToString(), OperatorName(item))), state.Operator.ToString());
        Control value = ValueEditor(selected, state);
        Button remove = Command("×");
        property.SelectionChanged += (_, _) =>
        {
            if (SelectedKey(property) is not { } identifier)
            {
                return;
            }

            SemanticQueryProperty changed = properties.Single(candidate => candidate.Identifier == identifier);
            state.Property = identifier;
            state.Operator = changed.Operators.FirstOrDefault();
            state.Value = String.Empty;
            Rebuild();
        };
        comparison.SelectionChanged += (_, _) =>
        {
            if (Enum.TryParse(SelectedKey(comparison), out QueryComparisonOperator op))
            {
                state.Operator = op;
            }

            Rebuild();
        };
        remove.Click += (_, _) =>
        {
            group.Conditions.RemoveAt(index);
            Rebuild();
        };
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2*,1.2*,2*,Auto"),
            ColumnSpacing = 8,
            Children = { property, At(comparison, 1), At(value, 2), At(remove, 3) }
        };
    }

    private Control Relationship(QueryGroupState owner, int index, int depth)
    {
        RelationshipState state = owner.Relationships[index];
        IReadOnlyList<SemanticQueryRelationship> choices = Relationships(owner.Concept);
        SemanticQueryRelationship selected = choices.FirstOrDefault(item => item.Identifier == state.Relationship) ?? choices[0];
        IReadOnlyList<SemanticQueryConcept> targets = Targets(selected);
        SemanticQueryConcept target = targets.FirstOrDefault(item => item.Identifier == state.Target.Concept) ?? targets[0];
        ComboBox relationship = Selection(choices.Select(item => (item.Identifier, item.DisplayName)), selected.Identifier);
        ComboBox targetType = Selection(targets.Select(item => (item.Identifier, item.DisplayName)), target.Identifier);
        Button remove = Command("Remove Relationship");
        relationship.SelectionChanged += (_, _) =>
        {
            if (SelectedKey(relationship) is not { } identifier)
            {
                return;
            }

            SemanticQueryRelationship changed = choices.Single(item => item.Identifier == identifier);
            state.Relationship = identifier;
            state.Target = new QueryGroupState(DefaultTarget(changed));
            Rebuild();
        };
        targetType.SelectionChanged += (_, _) =>
        {
            if (SelectedKey(targetType) is not { } identifier)
            {
                return;
            }

            state.Target = new QueryGroupState(identifier);
            Rebuild();
        };
        remove.Click += (_, _) =>
        {
            owner.Relationships.RemoveAt(index);
            Rebuild();
        };
        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.5*,1.5*,Auto"),
            ColumnSpacing = 8,
            Children = { relationship, At(targetType, 1), At(remove, 2) }
        };
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(14, 10, 0, 0) };
        var existence = Selection(new[] { ("has", "Has a related entity matching"), ("none", "Has no related entity matching") }, state.Negated ? "none" : "has");
        existence.SelectionChanged += (_, _) =>
        {
            state.Negated = SelectedKey(existence) == "none";
            NotifyDraftChanged();
        };
        body.Children.Add(existence);
        body.Children.Add(Label($"Related {target.DisplayName}"));
        body.Children.Add(GroupEditor(state.Target, depth + 1, false));
        return new StackPanel { Spacing = 4, Children = { heading, body } };
    }

    private Control ValueEditor(SemanticQueryProperty property, ConditionState state)
    {
        if (state.Operator is QueryComparisonOperator.IsPresent or QueryComparisonOperator.IsMissing)
        {
            return Muted("No value required");
        }

        if (state.Operator == QueryComparisonOperator.In)
        {
            var list = new TextBox { Text = state.Value, AcceptsReturn = true, MinHeight = 68, PlaceholderText = "One value per line" };
            list.TextChanged += (_, _) =>
            {
                state.Value = list.Text ?? String.Empty;
                NotifyDraftChanged();
            };
            return list;
        }
        if (property.Editor == QueryValueEditor.Boolean)
        {
            ComboBox value = Selection([("true", "True"), ("false", "False")],
                Boolean.TryParse(state.Value, out bool parsed) ? parsed.ToString().ToLowerInvariant() : "true");
            state.Value = SelectedKey(value) ?? "true";
            value.SelectionChanged += (_, _) =>
            {
                state.Value = SelectedKey(value) ?? "true";
                NotifyDraftChanged();
            };
            return value;
        }
        if (property.Editor == QueryValueEditor.Number)
        {
            Decimal.TryParse(state.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed);
            // NumericUpDown displays zero for an empty draft. Keep the draft aligned with what the
            // user sees even when they accept that initial value without causing ValueChanged.
            state.Value = parsed.ToString(CultureInfo.InvariantCulture);
            var value = new NumericUpDown { Value = parsed, MinWidth = 150, FormatString = "0.################" };
            value.ValueChanged += (_, _) =>
            {
                state.Value = (value.Value ?? 0).ToString(CultureInfo.InvariantCulture);
                NotifyDraftChanged();
            };
            return value;
        }
        string prompt = property.Editor switch
        {
            QueryValueEditor.Date => "YYYY-MM-DD",
            QueryValueEditor.DateTime => "YYYY-MM-DDThh:mm:ssZ",
            QueryValueEditor.Identifier => "Identifier or URI",
            _ => "Value"
        };
        var text = new TextBox { Text = state.Value, MinWidth = 150, PlaceholderText = prompt };
        text.TextChanged += (_, _) =>
        {
            state.Value = text.Text ?? String.Empty;
            NotifyDraftChanged();
        };
        return text;
    }

    private SemanticQueryDocument Build()
    {
        QueryGroupState root = _root ?? throw new InvalidOperationException("The query has no primary concept.");
        SemanticQueryConcept concept = _metadata!.Concepts.Single(item => item.Identifier == root.Concept);
        SemanticQueryProjectionField[] projection = Properties(root.Concept).Where(item => _projection.Contains(item.Identifier))
            .Select(item => new SemanticQueryProjectionField(item, _projectionNames.GetValueOrDefault(item.Identifier) ?? Camel(item.DisplayName))).ToArray();
        return new(concept, BuildBlocks(root), projection, _first, _includeEvidence, _conceptMatchMode, _orderings.ToArray());
    }

    private SemanticQueryBlock[] BuildBlocks(QueryGroupState group)
    {
        IEnumerable<SemanticQueryBlock> conditions = group.Conditions.Select(item =>
        {
            SemanticQueryProperty property = Properties(group.Concept).Single(candidate => candidate.Identifier == item.Property);
            return (SemanticQueryBlock)new SemanticPropertyConditionBlock(NewId("condition"), property, item.Operator,
                item.Operator == QueryComparisonOperator.In
                    ? JsonSerializer.SerializeToElement(item.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(value => TypedValue(property.Editor, value.TrimEnd('\r'))).ToArray(), BuilderJsonContext.Default.JsonElementArray)
                    : item.Operator is QueryComparisonOperator.IsPresent or QueryComparisonOperator.IsMissing ? JsonSerializer.SerializeToElement((string?)null, BuilderJsonContext.Default.String)
                    : TypedValue(property.Editor, item.Value));
        });
        IEnumerable<SemanticQueryBlock> relationships = group.Relationships.Select(item =>
        {
            SemanticQueryRelationship relationship = Relationships(group.Concept).Single(candidate => candidate.Identifier == item.Relationship);
            SemanticQueryConcept target = _metadata!.Concepts.Single(candidate => candidate.Identifier == item.Target.Concept);
            return (SemanticQueryBlock)new SemanticRelationshipConditionBlock(NewId("relationship"), relationship, target,
                item.Target.Operator, BuildBlocks(item.Target), item.Negated);
        });
        return conditions.Concat(relationships).Concat(group.Groups.Select(nested =>
            (SemanticQueryBlock)new SemanticLogicalQueryBlock(NewId("group"), nested.Operator, BuildBlocks(nested)))).ToArray();
    }

    private void NotifyDraftChanged()
    {
        if (_root is null || _metadata is null)
        {
            return;
        }

        var query = Build();
        bool valid = SemanticQueryBuilderSupport.CanEdit(_metadata, query);
        if (_run is not null)
        {
            _run.IsEnabled = valid && !IsRunning;
        }

        if (_validation is not null)
        {
            _validation.Text = valid ? String.Empty : "Complete the condition values. Dates use YYYY-MM-DD; date-times need a timezone; lists need at least one valid value.";
            _validation.IsVisible = !valid;
        }
        DraftChanged?.Invoke(this, new(query));
    }

    private IReadOnlyList<SemanticQueryProperty> Properties(string concept) =>
        _metadata!.PropertiesByConcept.TryGetValue(concept, out IReadOnlyList<SemanticQueryProperty>? result) ? result : [];

    private IReadOnlyList<SemanticQueryRelationship> Relationships(string concept) =>
        _metadata!.RelationshipsByConcept.TryGetValue(concept, out IReadOnlyList<SemanticQueryRelationship>? result) ? result : [];

    private IReadOnlyList<SemanticQueryConcept> Targets(SemanticQueryRelationship relationship) => _metadata!.Concepts.Where(item =>
        relationship.TargetTypes is null || relationship.TargetTypes.Count == 0 || relationship.TargetTypes.Contains(item.Identifier, StringComparer.Ordinal)).ToArray();

    private string DefaultTarget(SemanticQueryRelationship relationship) => Targets(relationship).FirstOrDefault()?.Identifier ?? _metadata!.Concepts[0].Identifier;

    private static JsonElement TypedValue(QueryValueEditor editor, string value) => editor switch
    {
        QueryValueEditor.Boolean when Boolean.TryParse(value, out bool parsed) => JsonSerializer.SerializeToElement(parsed, BuilderJsonContext.Default.Boolean),
        QueryValueEditor.Number when Decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed) => JsonSerializer.SerializeToElement(parsed, BuilderJsonContext.Default.Decimal),
        _ => JsonSerializer.SerializeToElement(value, BuilderJsonContext.Default.String),
    };

    private static string DisplayValue(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? String.Empty : value.GetRawText();

    private static ComboBox Selection(IEnumerable<(string Key, string Name)> choices, string? selected)
    {
        ComboBoxItem[] items = choices.Select(item => new ComboBoxItem { Content = item.Name, Tag = item.Key }).ToArray();
        var selection = new ComboBox { ItemsSource = items, SelectedItem = items.FirstOrDefault(item => Equals(item.Tag, selected)) ?? items.FirstOrDefault() };
        selection.Classes.Add("ef-text-choice");
        return selection;
    }

    private static string? SelectedKey(ComboBox selection) => selection.SelectedItem is ComboBoxItem item ? item.Tag as string : null;

    private static Button Command(string text)
    {
        var button = new Button { Content = text };
        button.Classes.Add("ef-command");
        return button;
    }

    private static TextBlock Label(string text) => new() { Text = text, Classes = { "ef-field-label" }, VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock Muted(string text)
    {
        var result = new TextBlock { Text = text, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
        result.Classes.Add("ef-muted");
        return result;
    }

    private static T At<T>(T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static string OperatorName(QueryComparisonOperator value) => value switch
    {
        QueryComparisonOperator.NotEquals => "does not equal",
        QueryComparisonOperator.IsPresent => "is present",
        QueryComparisonOperator.IsMissing => "is missing",
        QueryComparisonOperator.In => "is one of",
        QueryComparisonOperator.StartsWith => "starts with",
        QueryComparisonOperator.GreaterThan => "greater than",
        QueryComparisonOperator.GreaterThanOrEqual => "is at least",
        QueryComparisonOperator.LessThan => "less than",
        QueryComparisonOperator.LessThanOrEqual => "is at most",
        _ => value.ToString().ToLowerInvariant()
    };

    private static string Camel(string value) => String.Concat(value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select((word, index) => index == 0 ? word.ToLowerInvariant() : Char.ToUpperInvariant(word[0]) + word[1..]));

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private sealed class QueryGroupState(string concept)
    {
        public string Concept { get; } = concept;

        public QueryLogicalOperator Operator { get; set; } = QueryLogicalOperator.All;

        public List<QueryGroupState> Groups { get; } = [];

        public List<ConditionState> Conditions { get; } = [];

        public List<RelationshipState> Relationships { get; } = [];
    }

    private sealed class ConditionState(string property, QueryComparisonOperator @operator, string value)
    {
        public string Property { get; set; } = property;

        public QueryComparisonOperator Operator { get; set; } = @operator;

        public string Value { get; set; } = value;
    }

    private sealed class RelationshipState(string relationship, QueryGroupState target)
    {
        public bool Negated
        {
            get; set;
        }

        public string Relationship { get; set; } = relationship;

        public QueryGroupState Target { get; set; } = target;
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(SemanticQueryDocument))]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonElement[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
[System.Text.Json.Serialization.JsonSerializable(typeof(decimal))]
internal sealed partial class BuilderJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
