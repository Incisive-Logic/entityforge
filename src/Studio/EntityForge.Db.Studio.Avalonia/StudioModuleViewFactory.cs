using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using EntityForge.Db.Studio.Modules;
using EntityForge.Db.AppModel.Connections;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.AppModel.Optimizations;
using EntityForge.Db.Semantic;
using EntityForge.Db.AppModel.Avalonia.Queries;
using EntityForge.Db.AppModel.Avalonia.Catalogs;
using EntityForge.Db.AppModel.Avalonia.Results;
using EntityForge.Db.Client;
using EntityForge.Db;
using EntityForge.Db.Studio.Workspace;
using EntityForge.Db.Studio.Results;
using EntityForge.UI.Workspace;

namespace EntityForge.Db.Studio.Avalonia;

internal sealed class StudioModuleViewFactory(
    EntityForgeConnectionSession connection,
    EntityQuerySession query,
    NativeQuerySession nativeQuery,
    SemanticPropertyOptimizationSession optimizer,
    EntityForge.Db.AppModel.Storage.StorageProfileSession storage)
{
    public NativeCatalogueSession? NativeCatalogue
    {
        get; init;
    }

    public StudioHostServices HostServices { get; init; } = StudioHostServices.CreateTransient();

    public StudioPanelSessions.QueryState? QueryState
    {
        get; init;
    }

    private bool UseGraphQl => QueryState?.UseGraphQl == true;

    private Control? _queryActions;

    public StudioPanelViewState ViewState { get; init; } = new();

    private readonly List<Action> _captureViewState = [];

    public void CaptureViewState()
    {
        foreach (var capture in _captureViewState)
        {
            capture();
        }
    }

    public Action<EntityProjectionQueryResult, EntityForge.Query.Projection.EntityProjectionNode>? EntitySelected
    {
        get; init;
    }

    public StudioPanelSessions.SelectionState? Selection
    {
        get; init;
    }

    public Action? RefreshRequested
    {
        get; init;
    }

    public Action<SemanticQueryConcept>? QueryConceptRequested
    {
        get; init;
    }

    public Action<string>? NativeBrowseRequested
    {
        get; init;
    }

    private readonly List<(WeakReference<TextBlock> Label, Func<string> Text)> _draftWarnings = [];

    public void RefreshDraftWarnings()
    {
        _draftWarnings.RemoveAll(item => !item.Label.TryGetTarget(out _));
        foreach (var item in _draftWarnings)
        {
            if (item.Label.TryGetTarget(out TextBlock? label))
            {
                label.Text = item.Text();
                label.IsVisible = !String.IsNullOrWhiteSpace(label.Text);
            }
        }
    }

    private TextBlock DraftWarning(Func<string> text)
    {
        var label = new TextBlock { Text = text(), TextWrapping = TextWrapping.Wrap };
        label.IsVisible = !String.IsNullOrWhiteSpace(label.Text);
        label.Classes.Add("ef-muted");
        _draftWarnings.Add((new(label), text));
        return label;
    }

    public Control Create(WorkspaceModuleNode node)
    {
        if (node.ModuleKey == StudioModuleKeys.Telemetry)
        {
            var tabs = new TabControl
            {
                SelectedIndex = ViewState.TelemetryTab,
                ItemsSource = new[]
            {
                new TabItem { Header = "Live metrics", Content = new TelemetryControl(connection) },
                new TabItem { Header = "History", Content = new HistoryControl(connection) }
            }
            };
            _captureViewState.Add(() =>
            {
                if (tabs.IsAttachedToVisualTree())
                {
                    ViewState.TelemetryTab = Math.Max(0, tabs.SelectedIndex);
                }
            });
            return new Border { Padding = new Thickness(14, 6, 14, 14), Child = tabs };
        }
        var body = new StackPanel { Spacing = 10 };
        foreach (Control control in ContentFor(node.ModuleKey))
        {
            body.Children.Add(control);
        }

        var content = new ScrollViewer { Content = body, Padding = new Thickness(14) };
        Control root = content;
        if (node.ModuleKey == StudioModuleKeys.Query)
        {
            _captureViewState.Add(ViewState.TrackScroll(UseGraphQl ? "graphql" : nativeQuery.Language.ToString() + "-builder", content));
            return new QueryPanelControl(content, _queryActions);
        }
        if (body.Children.Count == 1 && body.Children[0] is InspectionPanel inspection)
        {
            body.Children.Remove(inspection);
            return new Border { Padding = new Thickness(14, 6, 14, 14), Child = inspection };
        }
        if (node.ModuleKey == StudioModuleKeys.Results && body.Children.LastOrDefault() is ResultPresentationControl presentation)
        {
            // Keep paging, warnings and view selection outside the result viewport.
            body.Children.Remove(presentation);
            content.Content = null;
            var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            void UpdateHeaderSpacing() => body.Margin = new Thickness(0, 0, 0,
                body.Children.Any(child => child.IsVisible) ? 8 : 0);
            foreach (var child in body.Children)
            {
                child.PropertyChanged += (_, change) =>
                {
                    if (change.Property == Visual.IsVisibleProperty)
                    {
                        UpdateHeaderSpacing();
                    }
                };
            }

            UpdateHeaderSpacing();
            layout.Children.Add(body);
            Grid.SetRow(presentation, 1);
            layout.Children.Add(presentation);
            root = new Border { Padding = new Thickness(14, 6, 14, 14), Child = layout };
            content = presentation.ResultsScroller;
        }
        if (node.ModuleKey == StudioModuleKeys.Results && QueryState?.ActiveResult is { } page && Selection is not null)
        {
            var view = Selection.Exploration.Current(page);
            double offset = view.ScrollOffset;
            bool restored = false;
            var resultControls = root.GetLogicalDescendants().OfType<EntityProjectionResultsControl>().ToArray();
            foreach (var resultControl in resultControls)
            {
                resultControl.RenderingChanged += () =>
                {
                    if (resultControl.IsRendering)
                    {
                        restored = false;
                        return;
                    }
                    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        content.Offset = new Vector(0, offset);
                        restored = true;
                    }, global::Avalonia.Threading.DispatcherPriority.Loaded);
                };
            }

            content.AttachedToVisualTree += (_, _) => global::Avalonia.Threading.Dispatcher.UIThread.Post(
                () =>
                {
                    content.Offset = new Vector(0, offset);
                    restored = true;
                }, global::Avalonia.Threading.DispatcherPriority.Loaded);
            content.DetachedFromVisualTree += (_, _) => restored = false;
            content.ScrollChanged += (_, _) =>
            {
                if (restored && !resultControls.Any(control => control.IsRendering))
                {
                    view.ScrollOffset = content.Offset.Y;
                }
            };
        }
        if (node.ModuleKey == StudioModuleKeys.Evidence && Selection is { } selection)
        {
            double offset = selection.EvidenceScrollOffset;
            bool restored = false;
            content.AttachedToVisualTree += (_, _) => global::Avalonia.Threading.Dispatcher.UIThread.Post(
                () =>
                {
                    content.Offset = new Vector(0, offset);
                    restored = true;
                }, global::Avalonia.Threading.DispatcherPriority.Loaded);
            content.DetachedFromVisualTree += (_, _) => restored = false;
            content.ScrollChanged += (_, _) =>
            {
                if (restored)
                {
                    selection.EvidenceScrollOffset = content.Offset.Y;
                }
            };
        }
        return root;
    }

    private IEnumerable<Control> ContentFor(string key) => key switch
    {
        StudioModuleKeys.Explorer => BrowserContent(),
        StudioModuleKeys.Query => QueryContent(),
        StudioModuleKeys.Results => QueryResultContent(),
        StudioModuleKeys.Evidence => EvidenceContent(),
        StudioModuleKeys.Status => [new OperationalStatusControl(connection)],
        StudioModuleKeys.Storage => StoragePanelContent(),
        StudioModuleKeys.Telemetry => [new TelemetryControl(connection)],
        StudioModuleKeys.Profiler => [new QueryProfilerControl(connection)],
        StudioModuleKeys.Optimizer => [new SemanticOptimizerControl(connection, optimizer)],
        StudioModuleKeys.Transactions => [new OperationLookupControl(connection)],
        StudioModuleKeys.Security => [new AccessControl(connection)],
        _ => [Muted("No view is registered for this module.")],
    };

    private Control[] StoragePanelContent()
    {
        var diagnostics = new StackPanel { Spacing = 10 };
        foreach (var control in StorageContent())
        {
            diagnostics.Children.Add(control);
        }

        return [new AdministrationControl(connection), new Expander { Header = "Storage diagnostics", Content = diagnostics }];
    }

    private Control[] StorageContent()
    {
        if (connection.CanAdminister == false)
        {
            return [Warning("The server reports that this credential cannot administer the database. Storage diagnostics and probes require administration access.")];
        }

        if (!storage.IsAvailable)
        {
            return [Muted("This host does not advertise storage profiling. Connect to a supported database host. Gateway storage is not a measurement of remote owners.")];
        }

        if (storage.IsBusy)
        {
            return [Muted("Reading server storage diagnostics…"), new ProgressBar { IsIndeterminate = true }, CancelButton(storage.Cancel)];
        }

        var items = new List<Control> { Muted("SERVER STORAGE · MANUAL DIAGNOSTICS") };
        if (storage.Error is { } error)
        {
            items.Add(Warning(error));
        }

        if (storage.Report is { } report)
        {
            items.Add(Muted($"Captured {report.CapturedAt.LocalDateTime:G} · {report.DataDirectory}"));
            items.Add(Row("▥", "Filesystem", report.FileSystem ?? "Unavailable", "ef-muted"));
            items.Add(Row("▥", "Available / total", $"{Size(report.AvailableBytes)} / {Size(report.TotalBytes)}", "ef-muted"));
            if (report.CapacityError is { } capacityError)
            {
                items.Add(Warning(capacityError));
            }

            if (report.ProbeRunning)
            {
                items.Add(Muted("A probe is running on the server. Refresh to retrieve its result."));
            }

            if (report.LastProbe is { } probe)
            {
                items.Add(Muted($"LAST PROBE · {probe.CompletedAt.LocalDateTime:G} · {probe.DurableWriteSamples} durable writes of {probe.DurableWriteBytes / 1024} KiB"));
                items.Add(Row("⌁", "Durable write median", $"{probe.MedianDurableWriteMs:N2} ms", "ef-muted"));
                items.Add(Row("⌁", "Durable write p95", $"{probe.P95DurableWriteMs:N2} ms", "ef-muted"));
                items.Add(Row("⌁", "Durable write p99 / maximum", $"{probe.P99DurableWriteMs:N2} / {probe.MaximumDurableWriteMs:N2} ms", "ef-muted"));
                items.Add(Row("⌁", "Sequential write + final flush", $"{probe.SequentialWriteMiBPerSecond:N1} MiB/s", "ef-muted"));
                items.Add(Row("⌁", "Cached sequential read", $"{probe.CachedReadMiBPerSecond:N1} MiB/s", "ef-muted"));
            }
            if (report.NextProbeAllowedAt is { } next && next > DateTimeOffset.UtcNow)
            {
                items.Add(Muted($"Next probe available after {next.LocalDateTime:T}. Refresh to check."));
            }
        }
        items.Add(StorageCommand("Refresh Storage Profile", storage.RefreshAsync));
        items.Add(Muted("Capacity is reported by the server filesystem; a VM or container may not expose the physical host's remaining space. A short probe cannot rule out rare stalls or establish database capacity."));
        items.Add(Muted("The probe writes about 4.25 MiB to a separate temporary file beside the database, then deletes it. It can compete with live traffic. Reads include the OS cache. Durable writes include buffered writes and Flush(true); this is not the exact WAL path. No cache flushing or device settings are changed."));
        if (storage.Report is { ProbeRunning: false } current && (current.NextProbeAllowedAt is null || current.NextProbeAllowedAt <= DateTimeOffset.UtcNow))
        {
            var review = new CheckBox { Content = "Run a bounded I/O test on the server; it may affect live traffic." };
            var command = StorageCommand("Run Storage Probe", storage.ProbeAsync);
            command.IsEnabled = false;
            review.IsCheckedChanged += (_, _) => command.IsEnabled = review.IsChecked == true && !storage.IsBusy;
            items.Add(new StackPanel { Spacing = 6, Children = { review, command } });
        }
        return items.ToArray();
    }

    private static Button StorageCommand(string label, Func<Task> action)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left };
        button.Classes.Add("ef-command");
        button.Click += async (_, _) => await action();
        return button;
    }

    private static string Size(long? bytes) => bytes is { } value ? $"{value / 1073741824d:N2} GiB" : "Unavailable";

    private static Button CancelButton(Action cancel)
    {
        var button = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => cancel();
        return button;
    }

    private Control[] BrowserContent()
    {
        EntityForgeConnectionSnapshot? current = connection.Current;
        if (current is null)
        {
            return [];
        }

        EntityForgeDatabaseProfile profile = current.Capabilities.Profile;
        if (profile == EntityForgeDatabaseProfile.Rdf)
        {
            return NativeBrowser("RDF data", "Inspect triples in the default graph or explore named graphs.",
                ("Browse default graph", "SELECT ?s ?p ?o\nWHERE { ?s ?p ?o }\nLIMIT 50"),
                ("Browse named graphs", "SELECT ?g ?s ?p ?o\nWHERE { GRAPH ?g { ?s ?p ?o } }\nLIMIT 50"));
        }

        if (profile == EntityForgeDatabaseProfile.PropertyGraph)
        {
            return NativeBrowser("Property graph", "Inspect vertices and the relationships connecting them.",
                ("Browse vertices", "MATCH (n)\nRETURN n\nLIMIT 50"),
                ("Browse relationships", "MATCH (a)-[e]->(b)\nRETURN a, e, b\nLIMIT 50"));
        }

        SemanticCatalog? catalog = connection.SemanticCatalog;
        if (catalog is null)
        {
            return [Muted(connection.CatalogStatus ?? "Vocabulary is unavailable.")];
        }

        var explorer = new SemanticCatalogExplorerControl();
        explorer.SetCatalog(catalog);
        if (ViewState.Catalogue is { } saved)
        {
            explorer.RestoreViewState(saved);
        }

        _captureViewState.Add(() =>
        {
            if (explorer.IsAttachedToVisualTree())
            {
                ViewState.Catalogue = explorer.CaptureViewState();
            }
        });
        explorer.QueryConceptRequested += (_, args) =>
        {
            SemanticQueryConcept? concept = connection.SemanticQueryMetadata?.Concepts
                .FirstOrDefault(item => item.Identifier == args.Concept.Identifier.AbsoluteUri);
            if (concept is not null)
            {
                QueryConceptRequested?.Invoke(concept);
            }
        };
        return [explorer];
    }

    private Control[] NativeBrowser(string title, string description, params (string Title, string Query)[] starts)
    {
        var items = new List<Control> { Label(title), Muted(description) };
        foreach (var start in starts)
        {
            var button = new Button
            {
                Content = start.Title,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = connection.NativeQueryExecutor is not null
            };
            button.Classes.Add("ef-command");
            button.Click += (_, _) => NativeBrowseRequested?.Invoke(start.Query);
            items.Add(button);
        }
        items.Add(Muted("Choose a starting point to open a bounded query. Records are displayed in Results."));
        if (NativeCatalogue is not null && connection.NativeQueryExecutor is not null)
        {
            items.Add(new NativeCatalogueControl(NativeCatalogue, NativeBrowseRequested));
        }

        if (connection.NativeQueryExecutor is null)
        {
            items.Add(Muted("This host does not advertise the query service needed for these browsing actions."));
        }

        return items.ToArray();
    }

    private Control[] QueryContent()
    {
        var content = QueryBuilderContent();
        string Profile() => connection.Current?.Capabilities.Profile switch
        {
            var p when p == EntityForgeDatabaseProfile.Semantic => "semantic",
            var p when p == EntityForgeDatabaseProfile.PropertyGraph => "property-graph",
            var p when p == EntityForgeDatabaseProfile.Rdf => "rdf",
            _ => "unknown"
        };
        var presets = new QueryPresetControl(HostServices, (name, variables) =>
        {
            string profile = Profile();
            bool builder = profile == "semantic" && !UseGraphQl;
            return new(Guid.NewGuid().ToString("N"), name, profile,
                builder ? "builder" : profile == "semantic" ? "graphql" : profile == "rdf" ? "sparql" : "gql",
                builder ? "" : nativeQuery.Draft, variables && !builder && profile == "semantic" ? nativeQuery.Variables : "{}",
                builder ? query.Draft : null);
        }, preset =>
        {
            if (preset.Profile != Profile())
            {
                throw new InvalidOperationException("Connect to a " + preset.Profile + " database to load this preset.");
            }

            if (query.State == EntityQueryExecutionState.Running || nativeQuery.State == EntityQueryExecutionState.Running || nativeQuery.IsPaging || query.IsPaging)
            {
                throw new InvalidOperationException("Wait for the current query to finish before loading a preset.");
            }

            if (preset.Kind == "builder")
            {
                query.Prepare(preset.Builder!);
                QueryState?.SetGraphQl(false);
            }
            else
            {
                nativeQuery.Draft = preset.Text;
                nativeQuery.Variables = preset.Variables;
                QueryState?.SetGraphQl(preset.Kind == "graphql");
            }
            RefreshRequested?.Invoke();
        });
        if (content.FirstOrDefault() is EntityForge.UI.Avalonia.Controls.PanelTabs mode)
        {
            return [new WrapPanel { ItemSpacing = 12, LineSpacing = 6, Children = { mode, presets } }, .. content.Skip(1)];
        }

        return [presets, .. content];
    }

    private Control[] QueryBuilderContent()
    {
        EntityForgeConnectionSnapshot? current = connection.Current;
        if (current is null)
        {
            return [];
        }

        if (current.Capabilities.Profile != EntityForgeDatabaseProfile.Semantic)
        {
            return NativeQueryContent();
        }

        var mode = new EntityForge.UI.Avalonia.Controls.PanelTabs
        {
            ItemsSource = new[] { "Builder", "GraphQL" },
            SelectedIndex = UseGraphQl ? 1 : 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = query.State != EntityQueryExecutionState.Running && nativeQuery.State != EntityQueryExecutionState.Running
        };
        global::Avalonia.Automation.AutomationProperties.SetName(mode, "Query mode");
        mode.SelectionChanged += (_, _) => QueryState?.SetGraphQl(mode.SelectedIndex == 1);
        if (UseGraphQl)
        {
            return [mode, .. NativeQueryContent()];
        }

        if (!current.Ready || connection.QueryExecutor is null)
        {
            return [Muted(connection.CatalogStatus ?? (!current.Ready
                ? "The database is not ready for queries. Reconnect when it is ready."
                : "This host does not advertise the entity-query services required by the builder."))];
        }

        SemanticQueryMetadata? metadata = connection.SemanticQueryMetadata;
        if (metadata is null)
        {
            return [Muted("The Semantic catalogue did not provide query metadata.")];
        }

        var editor = new SemanticQueryBuilderControl
        {
            IsRunning = query.State == EntityQueryExecutionState.Running,
            IsEnabled = query.State != EntityQueryExecutionState.Running,
            ShowTechnicalDetails = false,
            ExecutionHost = new ContentControl(),
        };
        editor.SetQuery(metadata, query.Draft);
        _queryActions = query.State == EntityQueryExecutionState.Running
            ? CancelButton(query.Cancel) : editor.ExecutionHost;
        if (query.Draft is null && editor.CurrentQuery is { } initial)
        {
            query.EditDraft(initial);
        }

        editor.DraftChanged += (_, args) => query.EditDraft(args.Query);
        editor.QueryRequested += async (_, args) =>
        {
            if (query.CanExecute)
            {
                await query.ExecuteAsync(args.Query);
            }
        };
        return [mode, editor];
    }

    private Control[] QueryResultContent()
    {
        if (connection.Current?.Capabilities.Profile != EntityForgeDatabaseProfile.Semantic || UseGraphQl)
        {
            return NativeResultContent();
        }

        return [
            ExecutionFeedback(QueryState?.SemanticFeedback),
            DraftWarning(() => query.HasDraftChanges ? "Query changed — run again to update these results." : String.Empty),
            ..SemanticResultContent()];
    }

    private Control[] SemanticResultContent()
    {
        if (query.State == EntityQueryExecutionState.Idle)
        {
            return [Muted("Run the connected query to see results.")];
        }

        if (query.State == EntityQueryExecutionState.Running && !query.IsPaging)
        {
            return [Muted("QUERY RUNNING"), new ProgressBar { IsIndeterminate = true }, CancelButton(query.Cancel)];
        }

        if (query.State == EntityQueryExecutionState.Failed)
        {
            return [Warning(query.Error ?? "The query failed."), Muted("Review the query, then use Run Query to try again.")];
        }

        if (query.State == EntityQueryExecutionState.Cancelled)
        {
            return [Muted("Query cancelled.")];
        }

        EntityProjectionQueryResult result = query.Result!;
        var explorer = new EntityProjectionResultsControl
        {
            ShowSummary = false,
            Exploration = Selection?.Exploration,
            Explorer = connection.QueryExecutor as IEntityExplorer,
            IsCurrent = () => query.ContainsResult(result),
        };
        explorer.SetResult(result);
        explorer.EvidenceRequested += (source, entity) => EntitySelected?.Invoke(source, entity);
        explorer.NavigationChanged += () => RefreshRequested?.Invoke();
        var previousIcon = new PathIcon();
        previousIcon.Classes.Add("ef-page-previous");
        var previous = new Button { Content = previousIcon, IsEnabled = query.CanPreviousPage };
        previous.Classes.Add("ef-page-button");
        ToolTip.SetTip(previous, "Previous Page");
        global::Avalonia.Automation.AutomationProperties.SetName(previous, "Previous Page");
        previous.Click += (_, _) => query.PreviousPage();
        var nextIcon = new PathIcon();
        nextIcon.Classes.Add("ef-page-next");
        var next = new Button { Content = nextIcon, IsEnabled = query.CanNextPage };
        next.Classes.Add("ef-page-button");
        ToolTip.SetTip(next, "Next Page");
        global::Avalonia.Automation.AutomationProperties.SetName(next, "Next Page");
        next.Click += async (_, _) =>
        {
            if (query.IsPaging)
            {
                query.Cancel();
            }
            else if (!explorer.IsRendering)
            {
                await query.NextPageAsync();
            }
        };
        var pageLabel = new TextBlock { Text = $"Page {query.PageNumber}" };
        pageLabel.Classes.Add("ef-page-label");
        var progress = new EntityForge.UI.Avalonia.Controls.ProgressRing();
        void UpdatePagingActivity()
        {
            bool busy = query.IsPaging || explorer.IsRendering;
            progress.IsActive = busy;
            next.Content = busy ? progress : nextIcon;
            next.IsEnabled = query.IsPaging || (!explorer.IsRendering && query.CanNextPage);
            string action = query.IsPaging ? "Cancel Page Loading" : explorer.IsRendering ? "Displaying Page" : "Next Page";
            ToolTip.SetTip(next, action);
            global::Avalonia.Automation.AutomationProperties.SetName(next, action);
        }
        explorer.RenderingChanged += UpdatePagingActivity;
        UpdatePagingActivity();
        var pages = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { previous, pageLabel, next }
        };
        pages.Classes.Add("ef-pagination");
        var presentation = ResultPresentation(explorer, () => StudioResultSyntax.Format(result),
            pageSummary: PageSummary(result, pages));
        return query.Error is null ? [presentation] : [Warning(query.Error), presentation];
    }

    private Control[] EvidenceContent()
    {
        if (connection.Current?.Capabilities.Profile != EntityForgeDatabaseProfile.Semantic)
        {
            return [Muted("Inspect native graph evidence and RDF terms in Results. Semantic provenance is not inferred for these profiles.")];
        }

        return [
            DraftWarning(() => (UseGraphQl ? nativeQuery.HasDraftChanges : query.HasDraftChanges) ? "Query changed — this evidence belongs to the previous results." : String.Empty),
            ..SemanticEvidenceContent()];
    }

    private Control[] SemanticEvidenceContent()
    {
        if (Selection?.Result is not { } result || Selection.Entity is not { } entity)
        {
            return [Label("Inspect an entity's sources"),
                Muted("Choose Inspect Evidence on a result card to see its provenance here. Use a field's Evidence button to inspect that field inline.")];
        }

        var evidence = new EntityProjectionEvidenceControl { Expanded = Selection.ExpandedEvidence };
        evidence.SetResult(result, entity);
        return [Muted($"Evidence for {EntityProjectionResultsControl.PreferredTitle(entity)} · Snapshot {result.SnapshotVersion.Value:N0}"), evidence];
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold };

    private Control[] NativeQueryContent()
    {
        if (connection.NativeQueryExecutor is null)
        {
            return [Muted("Text queries are unavailable. The host must advertise a supported query endpoint.")];
        }

        if (nativeQuery.Language == NativeQueryLanguage.GraphQl)
        {
            return GraphQlContent();
        }

        var editor = new AvaloniaEdit.TextEditor
        {
            Text = nativeQuery.Draft,
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
            Height = 300,
            WordWrap = true,
            IsReadOnly = nativeQuery.State == EntityQueryExecutionState.Running
        };
        global::Avalonia.Automation.AutomationProperties.SetName(editor, nativeQuery.Language == NativeQueryLanguage.Gql ? "GQL query" : "SPARQL query");
        _captureViewState.Add(ViewState.TrackEditor(nativeQuery.Language + "-operation", editor));
        var document = editor.Document;
        var run = new Button { Content = "Execute Query", IsEnabled = nativeQuery.CanExecute };
        var limit = Warning($"Query text is limited to {HttpNativeQueryExecutor.MaximumQueryCharacters:N0} characters. Shorten it before executing.");
        void ValidateLength()
        {
            limit.IsVisible = editor.Text.Length > HttpNativeQueryExecutor.MaximumQueryCharacters;
            run.IsEnabled = nativeQuery.CanExecute && !limit.IsVisible;
        }
        editor.TextChanged += (_, _) =>
        {
            if (!ReferenceEquals(editor.Document, document))
            {
                return;
            }

            nativeQuery.Draft = editor.Text;
            ValidateLength();
        };
        ValidateLength();
        run.Classes.Add("ef-primary");
        run.Click += async (_, _) => await nativeQuery.ExecuteAsync();
        _queryActions = nativeQuery.State == EntityQueryExecutionState.Running ? CancelButton(nativeQuery.Cancel) : run;
        var examples = new StackPanel { Spacing = 6 };
        if (nativeQuery.Language == NativeQueryLanguage.Sparql)
        {
            Example("Inspect default graph", "SELECT ?s ?p ?o\nWHERE {\n  ?s ?p ?o\n}\nLIMIT 50");
            Example("Inspect named graphs", "SELECT ?g ?s ?p ?o\nWHERE {\n  GRAPH ?g {\n    ?s ?p ?o\n  }\n}\nLIMIT 50");
        }
        else
        {
            Example("Inspect vertices", "MATCH (n)\nRETURN n\nLIMIT 50");
            Example("Inspect relationships", "MATCH (a)-[e]->(b)\nRETURN a, e, b\nLIMIT 50");
        }
        void Example(string title, string text)
        {
            var button = new Button { Content = title, IsEnabled = nativeQuery.CanExecute };
            button.Click += (_, _) =>
            {
                editor.Text = text;
            };
            examples.Children.Add(button);
        }
        return [Label(nativeQuery.Language == NativeQueryLanguage.Gql ? "Graph GQL" : "RDF SPARQL"),
            examples, editor, limit];
    }

    private Control[] GraphQlContent()
    {
        var editor = new GraphQlEditor(connection.NativeQueryExecutor!, nativeQuery.Draft,
            nativeQuery.State == EntityQueryExecutionState.Running);
        _captureViewState.Add(ViewState.TrackEditor("graphql-operation", editor.TextEditor));
        editor.TextEditor.WordWrap = ViewState.WrapQueryLines;
        var wrap = new CheckBox { Content = "Wrap lines", IsChecked = ViewState.WrapQueryLines };
        wrap.IsCheckedChanged += (_, _) => editor.TextEditor.WordWrap = ViewState.WrapQueryLines = wrap.IsChecked == true;
        editor.TextChanged += (_, _) => nativeQuery.Draft = editor.Text;
        var variables = new AvaloniaEdit.TextEditor
        {
            Text = nativeQuery.Variables,
            Height = 140,
            ShowLineNumbers = true,
            FontSize = 14,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
            IsReadOnly = nativeQuery.State == EntityQueryExecutionState.Running
        };
        global::Avalonia.Automation.AutomationProperties.SetName(variables, "GraphQL variables JSON");
        variables.Options.ConvertTabsToSpaces = true;
        variables.Options.IndentationSize = 2;
        variables.TextArea.TextView.LineTransformers.Add(new JsonSyntaxColourizer());
        variables.ActualThemeVariantChanged += (_, _) => variables.TextArea.TextView.Redraw();
        _captureViewState.Add(ViewState.TrackEditor("graphql-variables", variables));
        var variablesDocument = variables.Document;
        variables.TextChanged += (_, _) =>
        {
            if (ReferenceEquals(variables.Document, variablesDocument))
            {
                nativeQuery.Variables = variables.Text ?? "{}";
            }
        };
        var inspect = new Button { Content = "Inspect Schema", IsEnabled = nativeQuery.CanExecute };
        inspect.Click += (_, _) => editor.Text = QueryTextFormatting.GraphQl("{ __schema { queryType { name fields { name description } } } }");
        var example = new Button { Content = "Query Entities", IsEnabled = nativeQuery.CanExecute };
        example.Click += (_, _) =>
        {
            editor.Text = QueryTextFormatting.GraphQl("query Entities($concept: URI!) { entities(concept: $concept, first: 20, includeEvidence: true) { nodes { identity types } pageInfo { endCursor hasNextPage snapshotVersion } } }");
            if (connection.SemanticQueryMetadata?.Concepts.FirstOrDefault() is { } concept)
            {
                variables.Text = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["concept"] = concept.Identifier },
                    GraphQlVariablesJsonContext.Default.DictionaryStringString);
            }
        };
        var format = new Button { Content = "Format Query", IsEnabled = nativeQuery.CanExecute };
        ToolTip.SetTip(format, "Format Query (Ctrl+Shift+F)");
        format.Click += (_, _) => editor.FormatDocument();
        var formatVariables = new Button { Content = "Format JSON", IsEnabled = nativeQuery.CanExecute };
        var variableError = Warning("");
        variableError.IsVisible = false;
        variables.TextChanged += (_, _) => variableError.IsVisible = false;
        formatVariables.Click += (_, _) =>
        {
            if (QueryTextFormatting.TryFormat(variables.Text ?? "{}", true, out var formatted, out var error))
            {
                variables.Document.Replace(0, variables.Document.TextLength, formatted);
            }
            else
            {
                variableError.Text = error;
                variableError.IsVisible = true;
            }
        };
        var run = new Button { Content = "Execute Operation", IsEnabled = nativeQuery.CanExecute };
        run.Classes.Add("ef-primary");
        run.Click += async (_, _) => await nativeQuery.ExecuteAsync();
        _queryActions = nativeQuery.State == EntityQueryExecutionState.Running ? CancelButton(nativeQuery.Cancel) : run;
        var variablesPanel = new Expander
        {
            Header = "Variables (JSON)",
            IsExpanded = ViewState.VariablesExpanded ?? nativeQuery.Variables != "{}",
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Muted("Named inputs for this query. Match each $variable in the query with a JSON key here, without the $. For example, $limit uses {\"limit\": 20}. Leave {} if your query has no variables."),
                    formatVariables, variables, variableError
                }
            }
        };
        _captureViewState.Add(() =>
        {
            if (variablesPanel.IsAttachedToVisualTree())
            {
                ViewState.VariablesExpanded = variablesPanel.IsExpanded;
            }
        });
        return [new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6, Children = { inspect, example, format, wrap } },
            editor, variablesPanel];
    }

    private Control[] NativeResultContent() => [ExecutionFeedback(QueryState?.NativeFeedback), .. NativeResultBody()];

    private static Control ExecutionFeedback(QueryExecutionFeedback? feedback)
    {
        string status = feedback?.State switch
        {
            EntityQueryExecutionState.Running => "Running…",
            EntityQueryExecutionState.Succeeded => "Completed",
            EntityQueryExecutionState.Empty => "Completed · no matches",
            EntityQueryExecutionState.Failed => "Failed",
            EntityQueryExecutionState.Cancelled => "Cancelled",
            _ => String.Empty
        };
        if (feedback?.Duration is { } elapsed)
        {
            status += $" · {elapsed.TotalMilliseconds:N0} ms client elapsed";
        }

        var label = Muted(status);
        label.IsVisible = status.Length != 0;
        ToolTip.SetTip(label, "Time observed by Studio, including the request and response. This is not server CPU time or rendering time.");
        return label;
    }

    private Control[] NativeResultBody()
    {
        if (nativeQuery.State == EntityQueryExecutionState.Running)
        {
            return [Muted("QUERY RUNNING"), new ProgressBar { IsIndeterminate = true }, CancelButton(nativeQuery.Cancel)];
        }

        if (nativeQuery.State is EntityQueryExecutionState.Failed or EntityQueryExecutionState.Cancelled)
        {
            return [Warning(nativeQuery.Error ?? "Query did not complete."), Muted("Review the query and variables before executing again.")];
        }

        if (nativeQuery.Result is not { } result)
        {
            return [Muted("Run a native read query to inspect server results.")];
        }

        if (result.Language == NativeQueryLanguage.GraphQl)
        {
            var warning = DraftWarning(() => nativeQuery.HasDraftChanges ? "Query changed — run again to update these results." : String.Empty);
            if (result.Entities is not { } entities)
            {
                return [warning,
                ResultPresentation(NativeResultInspector.Create("Response", result.Document, ViewState.InspectionFor(result)), () => StudioResultSyntax.Format(result))];
            }

            var explorer = new EntityProjectionResultsControl
            {
                ShowSummary = false,
                Exploration = Selection?.Exploration,
                Explorer = connection.QueryExecutor as IEntityExplorer,
                IsCurrent = () => ReferenceEquals(nativeQuery.Result?.Entities, entities),
            };
            explorer.SetResult(entities);
            explorer.EvidenceRequested += (source, entity) => EntitySelected?.Invoke(source, entity);
            explorer.NavigationChanged += () => RefreshRequested?.Invoke();
            var previous = new Button { Content = "Previous", IsEnabled = nativeQuery.CanPreviousPage };
            previous.Click += (_, _) => nativeQuery.PreviousPage();
            var next = new Button { Content = nativeQuery.IsPaging ? "Cancel" : "Next", IsEnabled = nativeQuery.IsPaging || nativeQuery.CanNextPage };
            next.Click += async (_, _) =>
            {
                if (nativeQuery.IsPaging)
                {
                    nativeQuery.Cancel();
                }
                else
                {
                    await nativeQuery.NextPageAsync();
                }
            };
            var pages = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { previous, next, new TextBlock { Text = $"Page {nativeQuery.PageNumber}", VerticalAlignment = VerticalAlignment.Center } }
            };
            var messages = new List<Control> { warning };
            if (nativeQuery.Error is { } error)
            {
                messages.Add(Warning(error));
            }

            if (entities.NextContinuation is not null && !GraphQlPaging.CanContinue(nativeQuery.ExecutedDraft ?? ""))
            {
                messages.Add(Muted("More results are available. Automatic paging requires a single entities query with a direct root field; use the returned cursor to continue this query manually."));
            }

            messages.Add(ResultPresentation(explorer, () => StudioResultSyntax.Format(result), pageSummary: PageSummary(entities, pages)));
            return messages.ToArray();
        }
        var view = new StackPanel
        {
            Spacing = 8,
            Children =
        {
            NativeResultInspector.Create("Result", result.Document, ViewState.InspectionFor(result))
        }
        };
        return [DraftWarning(() => nativeQuery.HasDraftChanges ? "Query changed — run again to update these results." : String.Empty),
            ResultPresentation(view, () => StudioResultSyntax.Format(result), pageSummary: NativeSummary(result))];
    }

    private static Control NativeSummary(NativeQueryResult result)
    {
        var parts = new List<string>();
        foreach (string key in new[] { "matches", "bindings", "nodes", "edges", "quads" })
        {
            if (result.Document.TryGetProperty(key, out var array) && array.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                parts.Add($"{array.GetArrayLength():N0} {key}");
            }
        }

        if (result.Document.TryGetProperty("value", out var value))
        {
            parts.Add($"ASK: {value}");
        }

        var summary = Muted(string.Join(" · ", parts) + " returned");
        ToolTip.SetTip(summary, "Counts describe this response, subject to the query's LIMIT and filters. They are not database totals.");
        return summary;
    }

    private static Control PageSummary(EntityProjectionQueryResult result, Control? paging = null)
    {
        var summary = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 12,
            LineSpacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (paging is not null)
        {
            summary.Children.Add(paging);
        }

        summary.Children.Add(new TextBlock
        {
            Text = result.Document.Roots.Count == 1 ? "1 entity" : $"{result.Document.Roots.Count:N0} entities",
            VerticalAlignment = VerticalAlignment.Center
        });
        var snapshot = Muted($"Snapshot {result.SnapshotVersion.Value:N0}");
        snapshot.VerticalAlignment = VerticalAlignment.Center;
        summary.Children.Add(snapshot);
        return summary;
    }

    private Control ResultPresentation(Control view, Func<string> format, Control? pageSummary = null) =>
        new ResultPresentationControl(view, format, Selection?.ShowResultSyntax == true,
            show =>
            {
                if (Selection is not null)
                {
                    Selection.ShowResultSyntax = show;
                }
            }, pageSummary);

    private static TextBlock Warning(string text)
    {
        var result = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        result.Classes.Add("ef-warning");
        return result;
    }

    private static string Short(string value)
    {
        int separator = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('#'));
        return separator >= 0 && separator < value.Length - 1 ? value[(separator + 1)..] : value;
    }

    private static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add("ef-muted");
        return block;
    }

    private static Control Metric(string label, string value) => new StackPanel
    {
        Children = { Muted(label), new TextBlock { Text = value, FontSize = 24, FontWeight = FontWeight.SemiBold } },
    };

    private static Control Row(string icon, string title, string detail, string colourClass)
    {
        var iconBlock = new TextBlock { Text = icon, Width = 28, VerticalAlignment = VerticalAlignment.Center };
        iconBlock.Classes.Add(colourClass);
        var row = new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(6),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Children =
                {
                    iconBlock,
                    AtColumn(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center }, 1),
                    AtColumn(Muted(detail), 2),
                },
            },
        };
        row.Classes.Add("ef-list-row");
        return row;
    }

    private static T AtColumn<T>(T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }
}
