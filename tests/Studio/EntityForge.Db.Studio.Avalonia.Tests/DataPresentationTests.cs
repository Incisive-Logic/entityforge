using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using EntityForge.Db.Studio.Avalonia;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using EntityForge.Db.AppModel.Connections;
using System.Net;
using EntityForge.Db.AppModel.Optimizations;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;

namespace EntityForge.Db.Studio.Avalonia.Tests;

[TestClass]
public sealed class DataPresentationTests
{
    [TestMethod]
    public void NativeInspectorCanReachEveryReturnedItemAndSummaryStaysOutsideScroll()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).WithInterFont().SetupWithoutStarting();
                using var json = JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(0, 250)) + "]");
                var inspectionState = new NativeInspectionState();
                var inspector = (Expander)NativeResultInspector.Create("Bindings", json.RootElement, inspectionState);
                var inspectionWindow = new Window { Width = 500, Height = 500, Content = new ScrollViewer { Content = inspector } };
                inspectionWindow.Show();
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var header = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(inspector)
                    .OfType<global::Avalonia.Controls.Primitives.ToggleButton>().Single(b => b.Name == "ExpanderHeader");
                var point = header.TranslatePoint(new Point(20, 16), inspectionWindow)!.Value;
                inspectionWindow.MouseDown(point, global::Avalonia.Input.MouseButton.Left);
                inspectionWindow.MouseUp(point, global::Avalonia.Input.MouseButton.Left);
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(inspector.IsExpanded, "A pointer click must expand the native result.");
                inspectionWindow.Close();
                Button Button(string name) => inspector.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, name));
                string[] Values() => inspector.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(b => b.Text!).ToArray();
                Assert.AreEqual(100, Values().Length);
                Assert.AreEqual("[0]: 0", Values()[0]);
                Button("Next").RaiseEvent(new RoutedEventArgs(global::Avalonia.Controls.Button.ClickEvent));
                Assert.AreEqual("[100]: 100", Values()[0]);
                Button("Next").RaiseEvent(new RoutedEventArgs(global::Avalonia.Controls.Button.ClickEvent));
                Assert.AreEqual(50, Values().Length);
                Assert.AreEqual("[249]: 249", Values()[^1]);
                Assert.IsFalse(Button("Next").IsEnabled);
                Button("Previous").RaiseEvent(new RoutedEventArgs(global::Avalonia.Controls.Button.ClickEvent));
                Assert.AreEqual("[100]: 100", Values()[0]);
                inspector = (Expander)NativeResultInspector.Create("Bindings", json.RootElement, inspectionState);
                Assert.IsTrue(inspector.IsExpanded);
                Assert.AreEqual("[100]: 100", Values()[0], "Rebuilding must retain the current inspection page.");
                Assert.IsFalse(((Expander)NativeResultInspector.Create("Bindings", json.RootElement, new NativeInspectionState())).IsExpanded);
                var summary = new TextBlock { Text = "250 bindings" };
                var result = new ResultPresentationControl(inspector, () => json.RootElement.GetRawText(), false, _ => { }, summary);
                Assert.IsFalse(result.ResultsScroller.GetLogicalDescendants().Contains(summary));
                Assert.IsTrue(result.GetLogicalDescendants().Contains(summary));
                VerifyAdministrationReview();
                VerifyOptimizerPaging();
                VerifyTelemetryRefreshPreservesInspection();
                VerifyObservabilityPanels();
                VerifyWorkspaceFailureAndNarrowConnection();
                VerifyPinnedQueryExecution();
                VerifyWorkspaceScrollRetention();
                VerifyEditorContinuity();
                VerifyFocusedPanelNavigation();
                VerifyCatalogueContinuity();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new AssertFailedException(failure.ToString());
        }
    }

    private static void VerifyCatalogueContinuity()
    {
        var company = new EntityForge.Db.Client.SemanticCatalogConcept(new Uri("https://example.test/company"), "Company", null, [], [], [], [], []);
        var employee = new EntityForge.Db.Client.SemanticCatalogConcept(new Uri("https://example.test/employee"), "Employee", null, [], [], [], [], []);
        var catalogue = new EntityForge.Db.Client.SemanticCatalog(1, [], [], [company, employee]);
        var explorer = new EntityForge.Db.AppModel.Avalonia.Catalogs.SemanticCatalogExplorerControl();
        explorer.SetCatalog(catalogue);
        var saved = new EntityForge.Db.AppModel.Avalonia.Catalogs.SemanticCatalogExplorerViewState("Employee",
            EntityForge.Db.AppModel.Catalogs.SemanticCatalogView.Alphabetical, employee.Identifier.AbsoluteUri, true, true);
        explorer.RestoreViewState(saved);
        Assert.AreEqual(saved, explorer.CaptureViewState());
        var replacement = new EntityForge.Db.AppModel.Avalonia.Catalogs.SemanticCatalogExplorerControl();
        replacement.SetCatalog(catalogue);
        replacement.RestoreViewState(explorer.CaptureViewState());
        Assert.AreEqual(saved, replacement.CaptureViewState());
        replacement.SetCatalog(new EntityForge.Db.Client.SemanticCatalog(2, [], [], [company]));
        replacement.RestoreViewState(saved);
        Assert.AreEqual(company.Identifier.AbsoluteUri, replacement.CaptureViewState().SelectedConcept);
        var window = new Window { Width = 460, Height = 700, Content = explorer };
        window.Show();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var search = explorer.GetLogicalDescendants().OfType<TextBox>().Single();
        search.Focus();
        search.SelectionStart = search.SelectionEnd = 3;
        explorer.RestoreViewState(saved with
        {
            Filter = "Employ"
        });
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var rebuiltSearch = explorer.GetLogicalDescendants().OfType<TextBox>().Single();
        Assert.IsTrue(rebuiltSearch.IsFocused, "Filtering must not interrupt keyboard input.");
        Assert.AreEqual(3, rebuiltSearch.SelectionStart);
        window.Close();
    }

    private static void VerifyFocusedPanelNavigation()
    {
        var workspace = new EntityForge.UI.Avalonia.Workspace.AdaptiveWorkspaceControl
        {
            PresentationMode = EntityForge.UI.Avalonia.Workspace.WorkspacePresentationMode.Focus,
            ModuleFactory = module => new TextBlock { Text = "Content: " + module.Id },
            Layout = new EntityForge.UI.Workspace.WorkspaceLayout(new EntityForge.UI.Workspace.WorkspaceSplitNode("root",
                EntityForge.UI.Workspace.WorkspaceSplitOrientation.Vertical, 0.5,
                new EntityForge.UI.Workspace.WorkspaceModuleNode("query", "Query"),
                new EntityForge.UI.Workspace.WorkspaceSplitNode("right", EntityForge.UI.Workspace.WorkspaceSplitOrientation.Vertical, 0.5,
                    new EntityForge.UI.Workspace.WorkspaceModuleNode("results", "Results"),
                    new EntityForge.UI.Workspace.WorkspaceModuleNode("browser", "Database Browser"))))
        };
        var window = new Window { Width = 240, Height = 700, Content = workspace };
        window.Show();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var navigation = workspace.GetLogicalDescendants().OfType<Button>().ToArray();
        Assert.IsTrue(navigation.Single(button => Equals(button.Content, "Database Browser")).Bounds.Y >
            navigation.Single(button => Equals(button.Content, "Query")).Bounds.Y, "Panel navigation must wrap instead of hiding later panels.");
        var results = workspace.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Results"));
        var point = results.TranslatePoint(new Point(results.Bounds.Width / 2, results.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, global::Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, global::Avalonia.Input.MouseButton.Left);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.AreEqual("results", workspace.ResponsivePreferences.PreferredFocusedModuleId);
        Assert.IsTrue(workspace.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Content: results"));
        workspace.RefreshModules();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(workspace.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Content: results"));
        window.Close();
    }

    private static void VerifyEditorContinuity()
    {
        var state = new StudioPanelViewState();
        var workspace = new EntityForge.UI.Avalonia.Workspace.AdaptiveWorkspaceControl();
        AvaloniaEdit.TextEditor? editor = null;
        Action? capture = null;
        string draft = "query { companies }";
        bool available = true;
        workspace.ModuleFactory = _ =>
        {
            if (!available)
            {
                capture = null;
                return new TextBlock { Text = "Connection unavailable." };
            }
            editor = new AvaloniaEdit.TextEditor { Text = draft, Height = 300 };
            capture = state.TrackEditor("graphql", editor);
            var current = editor;
            var currentDocument = current.Document;
            current.TextChanged += (_, _) =>
            {
                if (ReferenceEquals(current.Document, currentDocument))
                {
                    draft = current.Text;
                }
            };
            return current;
        };
        workspace.Rebuilding += (_, _) => capture?.Invoke();
        workspace.Layout = new EntityForge.UI.Workspace.WorkspaceLayout(new EntityForge.UI.Workspace.WorkspaceModuleNode("query", "query"));
        var window = new Window { Width = 1000, Height = 400, Content = workspace };
        window.Show();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var document = editor!.Document;
        document.Replace(8, 9, "employees");
        editor.Select(8, 9);
        int caret = editor.CaretOffset;
        workspace.RefreshModules();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.AreSame(document, editor!.Document);
        Assert.AreEqual(8, editor.SelectionStart);
        Assert.AreEqual(9, editor.SelectionLength);
        Assert.AreEqual(caret, editor.CaretOffset);
        Assert.AreEqual("query { employees }", draft, "Detaching the old editor must not clear the draft.");
        document.UndoStack.Undo();
        Assert.AreEqual("query { companies }", editor.Text, "Undo must survive workspace reconstruction.");
        document.Replace(0, document.TextLength, "query {\n" + string.Join("\n", Enumerable.Range(0, 200).Select(i => $"  field{i}: __typename")) + "\n}");
        editor.CaretOffset = document.GetOffset(100, 4);
        window.UpdateLayout();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        editor.TextArea.TextView.EnsureVisualLines();
        editor.ScrollToLine(100);
        window.UpdateLayout();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        double scrollOffset = editor.VerticalOffset;
        int longCaret = editor.CaretOffset;
        Assert.IsTrue(scrollOffset > 0, $"Editor bounds {editor.Bounds}; view {editor.TextArea.TextView.Bounds}; lines {document.LineCount}.");
        workspace.RefreshModules();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(longCaret, editor!.CaretOffset);
        Assert.AreEqual(scrollOffset, editor.VerticalOffset, 1, "Long editor scroll position must survive a workspace rebuild.");
        available = false;
        workspace.RefreshModules();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        available = true;
        // Recovery invalidates several sessions, queuing rebuilds before editor Loaded restoration.
        workspace.RefreshModules();
        window.UpdateLayout();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs(global::Avalonia.Threading.DispatcherPriority.Loaded);
        workspace.RefreshModules();
        workspace.RefreshModules();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.AreSame(document, editor!.Document);
        Assert.AreEqual(longCaret, editor.CaretOffset);
        Assert.AreEqual(scrollOffset, editor.VerticalOffset, 1, "Recovery rebuilds must not capture an unrestored editor's zero offset.");
        window.Close();
    }

    private static void VerifyWorkspaceScrollRetention()
    {
        var state = new StudioPanelViewState();
        var workspace = new EntityForge.UI.Avalonia.Workspace.AdaptiveWorkspaceControl();
        ScrollViewer? scroll = null;
        Action? capture = null;
        double contentHeight = 1200;
        workspace.ModuleFactory = _ =>
        {
            scroll = new ScrollViewer { Content = new Border { Height = contentHeight } };
            capture = state.TrackScroll("builder", scroll);
            return scroll;
        };
        workspace.Rebuilding += (_, _) => capture?.Invoke();
        workspace.Layout = new EntityForge.UI.Workspace.WorkspaceLayout(new EntityForge.UI.Workspace.WorkspaceModuleNode("query", "query"));
        var window = new Window { Width = 1000, Height = 400, Content = workspace };
        window.Show();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        scroll!.Offset = new Vector(0, 300);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        workspace.RefreshModules();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(300, scroll!.Offset.Y, 1, "Refreshing modules must preserve editor position.");
        workspace.PresentationMode = EntityForge.UI.Avalonia.Workspace.WorkspacePresentationMode.Focus;
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(300, scroll!.Offset.Y, 1, "Responsive reconstruction must preserve editor position.");
        contentHeight = 100;
        workspace.RefreshModules();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(0, scroll!.Offset.Y);
        contentHeight = 1200;
        workspace.RefreshModules();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(300, scroll!.Offset.Y, 1, "An expanded view with no overflow must not erase the saved position.");
        window.Close();
    }

    private static void VerifyPinnedQueryExecution()
    {
        var actions = new ContentControl();
        var builder = new EntityForge.Db.AppModel.Avalonia.Queries.SemanticQueryBuilderControl { ExecutionHost = actions };
        var concept = new EntityForge.Db.AppModel.Queries.SemanticQueryConcept("urn:company", "Company");
        var metadata = new EntityForge.Db.AppModel.Queries.SemanticQueryMetadata([concept], new Dictionary<string, IReadOnlyList<EntityForge.Db.AppModel.Queries.SemanticQueryProperty>>(), new Dictionary<string, IReadOnlyList<EntityForge.Db.AppModel.Queries.SemanticQueryRelationship>>());
        builder.SetQuery(metadata);
        int executions = 0;
        builder.QueryRequested += (_, args) =>
        {
            executions++;
            Assert.AreEqual(concept, args.Query.PrimaryConcept);
        };
        var scroll = new ScrollViewer { Content = builder };
        var panel = new QueryPanelControl(scroll, actions);
        var window = new Window { Width = 460, Height = 250, Content = panel };
        window.Show();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Button Run() => actions.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Run Query"));
        Assert.IsFalse(scroll.GetLogicalDescendants().Contains(Run()), "Execution must not scroll with the editor.");
        var position = Run().TranslatePoint(default, panel)!.Value;
        Assert.IsTrue(position.Y >= 0 && position.Y + Run().Bounds.Height <= panel.Bounds.Height);
        scroll.Offset = new Vector(0, 100);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(scroll.Offset.Y > 0);
        Assert.AreEqual(position, Run().TranslatePoint(default, panel)!.Value, "Scrolling must not move execution controls.");
        Run().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(1, executions);
        builder.SetQuery(metadata); // A rebuilt builder must replace the external action without losing its handler.
        Run().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(2, executions);
        panel.RaiseEvent(new global::Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = global::Avalonia.Input.InputElement.KeyDownEvent,
            Key = global::Avalonia.Input.Key.Enter,
            KeyModifiers = global::Avalonia.Input.KeyModifiers.Control
        });
        Assert.AreEqual(3, executions, "Ctrl+Enter must use the same validated execution action.");
        window.Close();
    }

    private static void VerifyWorkspaceFailureAndNarrowConnection()
    {
        var store = new UnreadableWorkspaceStore();
        var view = new StudioView(StudioHostServices.CreateTransient() with
        {
            Workspaces = store
        });
        var window = new Window { Width = 460, Height = 600, Content = view };
        window.Show();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(view.FindControl<Border>("PersistenceNotice")!.IsVisible);
        Assert.IsTrue(view.FindControl<Border>("WelcomePanel")!.IsVisible);
        Assert.IsFalse(view.FindControl<EntityForge.UI.Avalonia.Workspace.AdaptiveWorkspaceControl>("Workspace")!.IsVisible);
        Assert.AreEqual("Retry load", view.FindControl<Button>("RetryPersistenceButton")!.Content);
        Assert.AreEqual(0, store.Writes);
        view.FindControl<Button>("ReplaceLayoutButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(1, store.Writes);
        Assert.IsFalse(view.FindControl<Border>("PersistenceNotice")!.IsVisible);
        view.FindControl<Button>("ConnectButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var overlay = view.FindControl<Border>("ModalOverlay")!;
        Assert.IsTrue(overlay.IsVisible);
        Assert.IsTrue(((ScrollViewer)overlay.Child!).Bounds.Width <= 460);
        view.RaiseEvent(new global::Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = global::Avalonia.Input.InputElement.KeyDownEvent,
            Key = global::Avalonia.Input.Key.Escape
        });
        Assert.IsFalse(overlay.IsVisible);
        window.Close();
    }

    private sealed class UnreadableWorkspaceStore : EntityForge.Db.Studio.Workspace.IStudioWorkspaceStore
    {
        public int Writes;

        public ValueTask<EntityForge.UI.Workspace.WorkspaceDocument?> LoadAsync(CancellationToken cancellationToken = default)
            => throw new IOException("Unreadable layout");

        public ValueTask SaveAsync(EntityForge.UI.Workspace.WorkspaceDocument document, CancellationToken cancellationToken = default)
        {
            Writes++;
            return ValueTask.CompletedTask;
        }
    }

    private static void VerifyAdministrationReview()
    {
        var host = new AdministrationFixture();
        using var connection = new EntityForgeConnectionSession(host);
        connection.ConnectAsync(new("fixture", "Fixture", new("https://example.test/")), "test-credential").GetAwaiter().GetResult();
        var control = new AdministrationControl(connection);
        Button Button(string name) => control.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, name));
        var run = Button("Run reviewed operation");
        var confirm = control.GetLogicalDescendants().OfType<CheckBox>().Single();
        Assert.IsFalse(run.IsVisible);
        Button("Review target").RaiseEvent(new RoutedEventArgs(global::Avalonia.Controls.Button.ClickEvent));
        Assert.IsTrue(run.IsVisible);
        Assert.IsFalse(run.IsEnabled);
        Assert.AreEqual(0, host.Posts);
        confirm.IsChecked = true;
        Assert.IsTrue(run.IsEnabled);
        control.GetLogicalDescendants().OfType<ComboBox>().Single().SelectedIndex = 1;
        Assert.IsFalse(run.IsVisible);
        Assert.AreEqual(false, confirm.IsChecked);
        Button("Review target").RaiseEvent(new RoutedEventArgs(global::Avalonia.Controls.Button.ClickEvent));
        Assert.IsTrue(run.IsVisible);
        Assert.IsFalse(run.IsEnabled);
        Assert.IsTrue(control.GetLogicalDescendants().OfType<TextBox>().Any(t => t.Text?.Contains("database-backup-") == true));
        // Merely reviewing a backup never sends a mutation.
        Assert.AreEqual(0, host.Posts);
    }

    private static void VerifyOptimizerPaging()
    {
        using var connection = new EntityForgeConnectionSession(new AdministrationFixture(semantic: true));
        connection.ConnectAsync(new("fixture", "Fixture", new("https://example.test/")), "test-credential").GetAwaiter().GetResult();
        using var optimizer = new SemanticPropertyOptimizationSession();
        optimizer.Bind(new OptimizerFixture());
        optimizer.RefreshAsync().AsTask().GetAwaiter().GetResult();
        var control = new SemanticOptimizerControl(connection, optimizer);
        var window = new Window { Width = 460, Height = 600, Content = control };
        window.Show();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Button Button(string name) => control.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, name));
        string[] Properties() => control.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(t => t.Text!).ToArray();
        Assert.AreEqual(20, Properties().Length);
        Assert.IsFalse(control.Viewport.GetLogicalDescendants().Contains(Button("Next")));
        Assert.IsTrue(control.Header.Bounds.Height < 220, "The fixed header should leave room for data in a narrow panel.");
        var evidence = control.GetLogicalDescendants().OfType<Expander>().First(e => Equals(e.Header, "Workload evidence"));
        evidence.IsExpanded = true;
        var selected = control.GetLogicalDescendants().OfType<SelectableTextBlock>().First();
        selected.SelectionStart = 0;
        selected.SelectionEnd = 8;
        optimizer.RefreshAsync().AsTask().GetAwaiter().GetResult();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.AreSame(evidence, control.GetLogicalDescendants().OfType<Expander>().First(e => Equals(e.Header, "Workload evidence")));
        Assert.IsTrue(evidence.IsExpanded);
        Assert.AreEqual(8, selected.SelectionEnd);
        Button("Next").RaiseEvent(new RoutedEventArgs(global::Avalonia.Controls.Button.ClickEvent));
        Assert.AreEqual("urn:test:property-20", Properties()[0]);
        Button("Next").RaiseEvent(new RoutedEventArgs(global::Avalonia.Controls.Button.ClickEvent));
        Assert.AreEqual(5, Properties().Length);
        Assert.AreEqual("urn:test:property-44", Properties()[^1]);
        Assert.IsFalse(Button("Next").IsEnabled);
        var filter = control.GetLogicalDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "Filter by property identifier or recommendation reason");
        filter.Text = "property-44";
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(1, Properties().Length);
        var mode = control.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.ItemsSource!.Cast<string>().First() == "Recommendations");
        mode.SelectedIndex = 1;
        Assert.AreEqual("urn:test:property-44", Properties().Single());
        filter.Text = "";
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(20, Properties().Length);
        Assert.IsTrue(control.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("Candidates:") == true));
        window.Close();
    }

    private static void VerifyTelemetryRefreshPreservesInspection()
    {
        using var connection = new EntityForgeConnectionSession(new AdministrationFixture());
        connection.ConnectAsync(new("fixture", "Fixture", new("https://example.test/")), "test-credential").GetAwaiter().GetResult();
        connection.RefreshTelemetryAsync().GetAwaiter().GetResult();
        var control = new TelemetryControl(connection);
        var window = new Window { Width = 460, Height = 600, Content = control };
        window.Show();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var name = control.GetLogicalDescendants().OfType<SelectableTextBlock>().Single();
        name.SelectionStart = 0;
        name.SelectionEnd = 4;
        var details = control.GetLogicalDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Metric details"));
        details.IsExpanded = true;
        connection.RefreshTelemetryAsync().GetAwaiter().GetResult();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.AreSame(name, control.GetLogicalDescendants().OfType<SelectableTextBlock>().Single());
        Assert.AreEqual(4, name.SelectionEnd);
        Assert.IsTrue(details.IsExpanded);
        Assert.IsTrue(control.Header.Bounds.Height < 220);
        Assert.IsFalse(control.Viewport.GetLogicalDescendants().OfType<Button>().Any(b => Equals(b.Content, "Next")));
        window.Close();
    }

    private static void VerifyObservabilityPanels()
    {
        using var connection = new EntityForgeConnectionSession(new AdministrationFixture());
        connection.ConnectAsync(new("fixture", "Fixture", new("https://example.test/")), "test-credential").GetAwaiter().GetResult();
        var history = new HistoryControl(connection);
        var window = new Window { Width = 460, Height = 600, Content = history };
        window.Show();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(1, history.GetLogicalDescendants().OfType<HistoryChart>().Count());
        var samples = history.GetLogicalDescendants().OfType<Expander>().Single();
        samples.IsExpanded = true;
        history.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Refresh")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.AreSame(samples, history.GetLogicalDescendants().OfType<Expander>().Single());
        Assert.IsTrue(samples.IsExpanded);
        Assert.IsTrue(history.Header.Bounds.Height < 220);
        window.Close();
        var profiler = new QueryProfilerControl(connection);
        window = new Window { Width = 460, Height = 600, Content = profiler };
        window.Show();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(profiler.Header.Bounds.Height < 220);
        Assert.IsFalse(profiler.Viewport.GetLogicalDescendants().OfType<Button>().Any(b => Equals(b.Content, "Next")));
        profiler.Body.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(1, profiler.GetLogicalDescendants().OfType<SpanTimeline>().Count());
        Assert.IsTrue(profiler.GetLogicalDescendants().OfType<SelectableTextBlock>().Any(t => t.Text == new string('a', 32)));
        window.Close();
    }

    private sealed class OptimizerFixture : ISemanticPropertyOptimizationClient
    {
        public ValueTask<SemanticPropertyOptimizationReport> AnalyzeAsync(CancellationToken cancellationToken = default)
        {
            var observations = Enumerable.Range(0, 45).Select(i => new SemanticPropertyWorkloadObservation(
                new(new Uri($"urn:test:property-{i}"), SemanticTermRole.Property), 50, 10, 2, 5000, 100,
                TimeSpan.FromMilliseconds(25), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(20),
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(20))).ToArray();
            return ValueTask.FromResult(new SemanticPropertyOptimizationReport(DateTimeOffset.UnixEpoch, observations,
                observations.Select(o => new SemanticPropertyOptimizationRecommendation(o.Property, SemanticPropertyStorageKind.ColdJson,
                    SemanticPropertyStorageKind.IndexedJson, 0.9, "Selective equality queries", o)).ToArray(), 45, 4096, 0, false));
        }

        public ValueTask<SemanticPropertyOptimizationResult> ApplyAsync(int maximumChanges = 1, CancellationToken cancellationToken = default) => throw new AssertFailedException("Browsing must not apply changes.");

        public ValueTask ResetObservationsAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException("Browsing must not reset observations.");
    }

    private sealed class AdministrationFixture(bool semantic = false) : HttpMessageHandler
    {
        public int Posts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
            }

            if (request.RequestUri!.AbsolutePath.StartsWith("/v1/semantic"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
            }

            string json = request.RequestUri!.AbsolutePath switch
            {
                "/v1/capabilities" => """{"product":"EntityForgeDB","version":"1","profile":"rdf","databaseIncarnation":"3f72501e-2e20-4cd6-b05f-9f7e5d3363f4","surfaces":[{"identifier":"administration","maturity":"Stable","features":["administration-jobs-v1","live-metrics-v1","observability-v1"]}]}""",
                "/v1/observability/configuration" => """{"history":true,"profiler":true,"instance":"test-db"}""",
                "/v1/observability/history" => """{"start":"2026-10-31T10:00:00Z","end":"2026-10-31T10:05:00Z","stepSeconds":5,"series":[{"name":"Queries","unit":"queries/s","points":[{"time":1793440800,"value":2}]}]}""",
                "/v1/observability/traces" => """{"start":"2026-10-31T10:00:00Z","end":"2026-10-31T10:05:00Z","limitReached":false,"traces":[{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","startedAt":"2026-10-31T10:00:00Z","durationMs":12}]}""",
                "/v1/observability/trace" => """{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","truncated":false,"spans":[{"id":"1234","parentId":"","name":"database.query","offsetMs":0,"durationMs":12,"outcome":"http-completed","route":"/graphql"}]}""",
                "/v1/telemetry" => """{"version":1,"processSession":"3f72501e-2e20-4cd6-b05f-9f7e5d3363f4","scope":"process","startedAt":"2026-10-31T10:00:00Z","capturedAt":"2026-10-31T10:05:00Z","maximumMetrics":128,"droppedMeasurements":0,"metrics":[{"meter":"EntityForge.Db","name":"entityforge.db.commits","kind":"Counter","samples":2,"sum":5,"minimum":2,"maximum":3,"last":3,"lastObservedAt":"2026-10-31T10:04:00Z"}]}""",
                "/v1/administration/target" => """{"version":1,"databaseIncarnation":"3f72501e-2e20-4cd6-b05f-9f7e5d3363f4","profile":"Rdf","dataDirectory":"/db","backupDirectory":"/backups"}""",
                _ => "{}"
            };
            if (semantic && request.RequestUri.AbsolutePath == "/v1/capabilities")
            {
                json = json.Replace("\"profile\":\"rdf\"", "\"profile\":\"semantic\"")
                    .Replace("\"surfaces\":[", "\"surfaces\":[{\"identifier\":\"semantic-catalog\",\"maturity\":\"Stable\",\"features\":[\"property-storage-optimization\"]},");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
