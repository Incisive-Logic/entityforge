using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EntityForge.Db.Studio.Modules;
using EntityForge.Db.AppModel.Connections;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.AppModel.Optimizations;
using EntityForge.Db;
using EntityForge.Db.Client;
using EntityForge.Db.Studio.Workspace;
using EntityForge.UI;
using EntityForge.UI.Avalonia;
using EntityForge.UI.Avalonia.Workspace;
using EntityForge.UI.Workspace;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Threading;

namespace EntityForge.Db.Studio.Avalonia;

public sealed partial class StudioView : UserControl
{
    private readonly StudioModuleRegistry _registry = StudioWorkspaceDefaults.CreateRegistry();
    private readonly StudioWorkspacePersistence _persistence;
    private readonly EntityForgeConnectionProfiles _connectionProfiles;
    private string _editingConnectionId = Guid.NewGuid().ToString("N");
    private bool _profileEditPending;
    private static readonly TimeSpan ConnectionCheckInterval = TimeSpan.FromSeconds(10);
    private readonly DispatcherTimer _connectionMonitor = new() { Interval = ConnectionCheckInterval };
    private bool _checkingConnection;
    private readonly Uri _defaultEndpoint;
    private string? _demoToken;
    private readonly EntityForgeConnectionSession _connection = new();
    private readonly SemanticPropertyOptimizationSession _optimizer = new();
    private Guid? _lastOptimizerReceipt;
    private readonly EntityForge.Db.AppModel.Storage.StorageProfileSession _storage = new();
    private readonly StudioConnectedPanels _moduleViews;
    private StudioWorkspaceSession _session = new(StudioWorkspaceDefaults.Create());
    private TaskCompletionSource<ConnectionOverlayResult?>? _connectionOverlayCompletion;

    public StudioView() : this(App.HostServices)
    {
    }

    public StudioView(StudioHostServices services)
    {
        InitializeComponent();
        InitializeSettings(services);
        _moduleViews = new StudioConnectedPanels(_registry, _connection, _optimizer, _storage, services);
        Workspace.Rebuilding += (_, _) => _moduleViews.CaptureViewState();
        _moduleViews.Presenter.Changed += () => Workspace.RefreshModules();
        _moduleViews.Presenter.SourceChanged += (target, source) =>
        {
            CaptureLayout();
            if (source is null)
            {
                _moduleViews.Presenter.Connections.Disconnect(target);
            }
            else
            {
                _moduleViews.Presenter.Connections.Connect(target, source);
            }

            _session.SetPanelConnections(_moduleViews.Presenter.Connections.Capture());
            _moduleViews.Configure(_session.Document);
            Save();
        };
        _moduleViews.Presenter.NameChanged += (id, name) =>
        {
            _session.NamePanel(id, name);
            _moduleViews.Configure(_session.Document);
            Workspace.RefreshModules();
            Save();
        };
        _moduleViews.QueryTargetRequested += content => new Flyout { Content = content }.ShowAt(Workspace);
        KeyDown += (_, e) =>
        {
            if (e.Key == global::Avalonia.Input.Key.Escape && ModalOverlay.IsVisible)
            {
                CompleteConnectionOverlay(null);
                e.Handled = true;
                return;
            }
            if (e.Key == global::Avalonia.Input.Key.Escape && _moduleViews.Presenter.Selecting is not null)
            {
                _moduleViews.Presenter.Cancel();
                e.Handled = true;
            }
        };
        _persistence = new(services.Workspaces);
        _connectionProfiles = new(services.ConnectionProfiles);
        _defaultEndpoint = services.DefaultEndpoint;
        if (_defaultEndpoint.IsLoopback)
        {
            _demoToken = services.TakeDemoToken?.Invoke();
        }

        Workspace.ModuleFactory = module =>
        {
            _moduleViews.Presenter.IsEditing = Workspace.IsEditing;
            return _moduleViews.Create(module);
        };
        Workspace.ModuleEditToolsFactory = module => _moduleViews.Presenter.CreateEditTools(
            new PanelId(_session.SelectedTab.Id, module.Id));
        _moduleViews.Presenter.EditLayoutRequested += () => SetLayoutEditing(true);
        Workspace.ModuleTitleFactory = module => _session.Document.PanelNames.FirstOrDefault(name =>
            name.Id == new PanelId(_session.SelectedTab.Id, module.Id))?.Name ?? _registry.Get(module.ModuleKey).DisplayName;
        Workspace.ModuleSplitRequested += SplitModule;
        Workspace.ModuleChangeRequested += ChangeModule;
        Workspace.ModuleRemoveRequested += RemoveModule;
        _moduleViews.Sessions.Changed += () => Dispatcher.UIThread.Post(Workspace.RefreshModules);
        _moduleViews.Sessions.DraftChanged += () => _moduleViews.RefreshDraftWarnings();
        _storage.Changed += (_, _) => Dispatcher.UIThread.Post(Workspace.RefreshModules);
        _connection.AdministrationChanged += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            if (_connection.AdministrationReceipt is { State: "Succeeded", Operation: "optimize" or "reset-observations" } receipt &&
                (receipt.Id != _lastOptimizerReceipt || _optimizer.Report is null) && _optimizer.CanRun)
            {
                _lastOptimizerReceipt = receipt.Id;
                await _optimizer.RefreshAsync();
            }
        });
        _connectionMonitor.Tick += CheckConnection;
        AttachedToVisualTree += (_, _) => _connectionMonitor.Start();
        AttachedToVisualTree += LoadWorkspace;
        DetachedFromVisualTree += (_, _) =>
        {
            _connectionMonitor.Stop();
            CompleteConnectionOverlay(null);
            CaptureAndSave();
            _moduleViews.Dispose();
            _optimizer.Dispose();
            _storage.Dispose();
            _connection.Dispose();
        };
        RenderDocument();
    }

    private async void LoadWorkspace(object? sender, global::Avalonia.VisualTreeAttachmentEventArgs args)
    {
        await LoadWorkspaceAsync();
        if (_demoToken is not null)
        {
            Connect(this, new RoutedEventArgs());
        }
    }

    private async Task LoadWorkspaceAsync()
    {
        WorkspaceDocument? saved = await _persistence.LoadAsync(document =>
        {
            foreach (var module in document.Tabs.SelectMany(t => WorkspaceLayoutTraversal.GetModules(t.Layout)))
            {
                _registry.Get(module.ModuleKey);
            }

            _ = new StudioWorkspaceSession(document).SelectedTab;
        });
        if (saved is not null)
        {
            _session = new StudioWorkspaceSession(NormalizeDefaultTabIcons(saved));
        }

        RenderDocument();
        RenderPersistenceStatus();
    }

    private static WorkspaceDocument NormalizeDefaultTabIcons(WorkspaceDocument document)
    {
        IReadOnlyDictionary<string, string> defaultIcons = StudioWorkspaceDefaults.Create().Tabs
            .ToDictionary(tab => tab.Id, tab => tab.IconKey, StringComparer.Ordinal);
        WorkspaceTab[] tabs = document.Tabs.Select(tab => defaultIcons.TryGetValue(tab.Id, out string? icon)
            ? tab with
            {
                IconKey = icon
            }
            : tab).ToArray();
        return new WorkspaceDocument(tabs, document.SelectedTabId, document.FormatVersion)
        {
            PanelConnections = document.PanelConnections,
            PanelNames = document.PanelNames
        };
    }

    private void ConfigurePanels()
    {
        _moduleViews.Configure(_session.Document);
        foreach (var panel in _moduleViews.Presenter.Connections.Panels)
        {
            if (!_session.Document.PanelNames.Any(name => name.Id == panel.Id))
            {
                _session.NamePanel(panel.Id, panel.Name);
            }
        }
    }

    private void RenderDocument()
    {
        ConfigurePanels();
        _session.SetPanelConnections(_moduleViews.Presenter.Connections.Capture());
        TabStrip.Children.Clear();
        foreach (WorkspaceTab tab in _session.Document.Tabs)
        {
            var button = new Button { Tag = tab.Id };
            button.Classes.Add("ef-tab");
            button.Classes.Set("active", tab.Id == _session.Document.SelectedTabId);
            button.Content = new StackPanel
            {
                Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                Spacing = 7,
                Children = { TabIcon(tab.IconKey), new TextBlock { Text = tab.Title } },
            };
            button.Click += SelectTab;
            TabStrip.Children.Add(button);
        }

        WorkspaceTab selected = _session.SelectedTab;
        TabName.Text = selected.Title;
        Workspace.ResponsivePreferences = selected.ResponsivePreferences;
        Workspace.Layout = selected.Layout;
    }

    private void SelectTab(object? sender, RoutedEventArgs args)
    {
        CaptureLayout();
        _session.Select((string)((Button)sender!).Tag!);
        RenderDocument();
        Save();
    }

    private void AddTab(object? sender, RoutedEventArgs args)
    {
        CaptureLayout();
        var layout = new WorkspaceLayout(new WorkspaceModuleNode($"module-{Guid.NewGuid():N}", StudioModuleKeys.Query));
        _session.Add("New Workspace", "workspace", layout);
        SetLayoutEditing(true);
        RenderDocument();
        Save();
    }

    private static Control TabIcon(string key) => new ShapePath
    {
        Width = 15,
        Height = 15,
        Stretch = Stretch.Uniform,
        Fill = Brush("EntityForge.Brush.TextMuted"),
        Data = Geometry.Parse(key switch
        {
            "database" or "▦" => "M3,2 L13,2 L13,14 L3,14 Z M5,4 L7,4 L7,6 L5,6 Z M9,4 L11,4 L11,6 L9,6 Z M5,8 L7,8 L7,10 L5,10 Z M9,8 L11,8 L11,10 L9,10 Z M5,12 L11,12 L11,13 L5,13 Z",
            "operations" or "⌁" => "M2,12 L5.5,8.5 L8,11 L14,5 L12.5,3.5 L8,8 L5.5,5.5 L1,10 Z",
            "security" or "⬡" => "M8,1 L14,3.5 L13,9.5 C12.5,12 10.5,14 8,15 C5.5,14 3.5,12 3,9.5 L2,3.5 Z M8,4 L5,5.2 L5.6,9 C6,10.4 6.9,11.4 8,12 C9.1,11.4 10,10.4 10.4,9 L11,5.2 Z",
            _ => "M2,2 L7,2 L7,7 L2,7 Z M9,2 L14,2 L14,7 L9,7 Z M2,9 L7,9 L7,14 L2,14 Z M9,9 L14,9 L14,14 L9,14 Z",
        }),
    };

    private void RenameTab(object? sender, RoutedEventArgs args)
    {
        if (String.IsNullOrWhiteSpace(TabName.Text))
        {
            return;
        }

        _session.Rename(_session.Document.SelectedTabId, TabName.Text);
        RenderDocument();
        Save();
    }

    private void RemoveTab(object? sender, RoutedEventArgs args)
    {
        CaptureLayout();
        _session.Remove(_session.Document.SelectedTabId);
        RenderDocument();
        Save();
    }

    private void SetLayoutEditing(bool editing)
    {
        _moduleViews.Presenter.IsEditing = editing;
        Workspace.IsEditing = editing;
        EditBar.IsVisible = editing;
        EditLayoutMenuItem.Header = editing ? "Finish Layout" : "Edit Layout";
    }

    private void ToggleEdit(object? sender, RoutedEventArgs args)
    {
        if (Workspace.IsEditing)
        {
            CaptureLayout();
        }

        SetLayoutEditing(!Workspace.IsEditing);
        if (!Workspace.IsEditing)
        {
            Save();
        }
    }

    private void ResetLayout(object? sender, RoutedEventArgs args)
    {
        _session.Reset();
        SetLayoutEditing(false);
        RenderDocument();
        Save();
    }

    private void SplitModule(object? sender, WorkspaceModuleSplitRequestedEventArgs args)
    {
        string id = $"module-{Guid.NewGuid():N}";
        Workspace.Layout = WorkspaceLayoutOperations.Split(Workspace.Layout!, args.Module.Id,
            new WorkspaceModuleNode(id, StudioModuleKeys.Results), args.Orientation, WorkspacePlacement.After);
        CaptureLayout();
        ConnectNewPanel(new PanelId(_session.SelectedTab.Id, id));
        Workspace.RefreshModules();
    }

    private void ConnectNewPanel(PanelId id)
    {
        if (!_moduleViews.Presenter.Connections.ConnectUniqueSourceOnTab(id))
        {
            return;
        }

        _session.SetPanelConnections(_moduleViews.Presenter.Connections.Capture());
        _moduleViews.Configure(_session.Document);
    }

    private void ChangeModule(object? sender, WorkspaceModuleRequestedEventArgs args)
    {
        StudioModuleDescriptor[] choices = _registry.Descriptors.OrderBy(item => item.DisplayName).ToArray();
        int current = Array.FindIndex(choices, item => item.Key == args.Module.ModuleKey);
        string next = choices[(current + 1) % choices.Length].Key;
        _session.ForgetPanel(new PanelId(_session.SelectedTab.Id, args.Module.Id));
        Workspace.Layout = WorkspaceLayoutOperations.ReplaceModule(Workspace.Layout!, args.Module.Id, next);
        CaptureLayout();
        ConnectNewPanel(new PanelId(_session.SelectedTab.Id, args.Module.Id));
        Workspace.RefreshModules();
    }

    private void RemoveModule(object? sender, WorkspaceModuleRequestedEventArgs args)
    {
        _session.ForgetPanel(new PanelId(_session.SelectedTab.Id, args.Module.Id));
        Workspace.Layout = WorkspaceLayoutOperations.RemoveModule(Workspace.Layout!, args.Module.Id);
        CaptureLayout();
        Workspace.RefreshModules();
    }

    private async void OpenEntityForgeWebsite(object? sender, RoutedEventArgs args)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(new Uri("https://entityforge.io"));
        }
    }

    private async void Connect(object? sender, RoutedEventArgs args)
    {
        if (!ConnectButton.IsEnabled)
        {
            return;
        }

        if (_connection.Current is not null)
        {
            _connection.Disconnect();
            _moduleViews.Bind(false);
            _optimizer.Bind(null);
            _storage.Bind(null);
            ShowDisconnected();
            Workspace.RefreshModules();
            return;
        }

        ConnectButton.IsEnabled = false;
        IReadOnlyList<EntityForgeConnectionProfile> profiles;
        try
        {
            profiles = await _connectionProfiles.LoadAsync();
        }
        catch (Exception exception)
        {
            ConnectButton.IsEnabled = true;
            await MessageAsync("Saved connections unavailable", exception.Message);
            return;
        }
        EntityForgeConnectionProfile initial = profiles.FirstOrDefault()
            ?? new EntityForgeConnectionProfile("local", "Local", _defaultEndpoint);
        string? demoToken = _demoToken;
        _demoToken = null;
        ConnectionOverlayResult? input = demoToken is null
            ? await ShowConnectionOverlayAsync(initial, profiles)
            : new("semantic-demo", "Semantic sample", _defaultEndpoint, demoToken, false);
        if (input is null)
        {
            ConnectButton.IsEnabled = true;
            return;
        }

        while (input is not null)
        {
            string? retryError = null;
            ConnectButton.IsEnabled = false;
            ConnectButton.Content = "Connecting…";
            ConnectionStatus.Text = "Discovering host…";
            try
            {
                var profile = new EntityForgeConnectionProfile(input.Id, input.Name, input.Endpoint);
                EntityForgeConnectionSnapshot snapshot = await _connection.ConnectAsync(profile, input.BearerToken);
                if (!ReferenceEquals(_connection.Current, snapshot))
                {
                    return;
                }

                _moduleViews.Bind(true);
                _storage.Bind(_connection.SupportsStorageProfile ? _connection.StorageProfile : null);
                _optimizer.Bind(_connection.SupportsOptimization
                    ? _connection.SemanticPropertyOptimizer : null);
                string? preferenceError = null;
                if (input.Remember)
                {
                    try
                    {
                        await _connectionProfiles.SaveAsync(snapshot.Profile);
                    }
                    catch (Exception exception)
                    {
                        preferenceError = $" · Profile not saved: {exception.Message}";
                    }
                }
                if (!ReferenceEquals(_connection.Current, snapshot))
                {
                    return;
                }

                ConnectionStatus.Text = $"{snapshot.Profile.Name} · {snapshot.Capabilities.Profile} · {(snapshot.Ready ? "Ready at connection time" : "Not ready")}";
                ConnectionLed.Fill = Brush(snapshot.Ready ? EntityForgeDesignTokens.Resource.Success : EntityForgeDesignTokens.Resource.Warning);
                ConnectButton.Content = "Disconnect";
                ConnectionNotice.IsVisible = false;
                WelcomePanel.IsVisible = false;
                Workspace.IsVisible = true;
                ConnectionStatus.Text += preferenceError;
                Workspace.RefreshModules();
                await _connection.RefreshOperationalStatusAsync();
            }
            catch (EntityForgeConnectionException exception)
            {
                ShowDisconnected();
                retryError = exception.Message;
            }
            catch (OperationCanceledException)
            {
                ShowDisconnected();
                Workspace.RefreshModules();
            }
            catch (Exception exception)
            {
                _connection.Disconnect();
                _moduleViews.Bind(false);
                _optimizer.Bind(null);
                _storage.Bind(null);
                ShowDisconnected();
                retryError = exception.Message;
            }
            finally
            {
                ConnectButton.IsEnabled = true;
            }
            if (retryError is null)
            {
                return;
            }

            ConnectButton.IsEnabled = false;
            input = await ShowConnectionOverlayAsync(new(input.Id, input.Name, input.Endpoint), profiles,
                "Connection failed. " + retryError + " Check the address and token, then try again.", input.Remember);
        }
        ConnectButton.IsEnabled = true;
    }

    private void RetryConnection(object? sender, RoutedEventArgs args) => CheckConnection(sender, args);

    private async void CheckConnection(object? sender, EventArgs args)
    {
        if (_checkingConnection || _connection.Current is not { } snapshot)
        {
            return;
        }

        _checkingConnection = true;
        RetryConnectionButton.IsEnabled = false;
        RetryConnectionButton.Content = "Checking…";
        try
        {
            var update = await _connection.CheckForUpdatesAsync();
            if (!ReferenceEquals(_connection.Current?.Profile, snapshot.Profile))
            {
                return;
            }

            bool available = _connection.Availability?.Available == true;
            if (!available)
            {
                ShowConnectionNotice(_connection.Error ?? "Connection unavailable. Retrying automatically.", false);
            }
            else if (update.AvailabilityChanged)
            {
                ShowConnectionNotice("Connection restored. Run your query again to refresh results.", true);
            }

            ConnectionStatus.Text = $"{snapshot.Profile.Name} · {(available ? "Connected" : "Connection unavailable · retrying…")}";
            ConnectionLed.Fill = Brush(available ? EntityForgeDesignTokens.Resource.Success : EntityForgeDesignTokens.Resource.Warning);
            if (update.CatalogChanged || update.SessionInvalidated)
            {
                _moduleViews.RefreshBindings(update.SessionInvalidated);
            }

            if (update.SessionInvalidated)
            {
                _storage.Bind(available && _connection.SupportsStorageProfile ? _connection.StorageProfile : null);
                _optimizer.Bind(available && _connection.SupportsOptimization ? _connection.SemanticPropertyOptimizer : null);
            }
            // Healthy, unchanged checks must not reconstruct editors or disturb ongoing work.
            if (update.CatalogChanged || update.SessionInvalidated || update.AvailabilityChanged)
            {
                Workspace.RefreshModules();
            }

            await _connection.RefreshOperationalStatusAsync();
            await _connection.RefreshAccessAsync();
        }
        finally
        {
            _checkingConnection = false;
            RetryConnectionButton.IsEnabled = true;
            RetryConnectionButton.Content = "Check now";
        }
    }

    private void ShowConnectionNotice(string message, bool recovered)
    {
        ConnectionNoticeText.Text = message;
        RetryConnectionButton.IsVisible = !recovered;
        DismissConnectionNoticeButton.IsVisible = recovered;
        ConnectionNotice.IsVisible = true;
    }

    private void DismissConnectionNotice(object? sender, RoutedEventArgs args) => ConnectionNotice.IsVisible = false;

    private void PopulateProfiles(IReadOnlyList<EntityForgeConnectionProfile> profiles, string? selected)
    {
        var items = profiles.Select(profile => new ComboBoxItem { Content = profile.Name, Tag = profile }).ToArray();
        SavedConnections.ItemsSource = items;
        SavedConnections.SelectedItem = items.FirstOrDefault(item => ((EntityForgeConnectionProfile)item.Tag!).Id == selected);
        RemoveConnectionButton.IsEnabled = SavedConnections.SelectedItem is not null;
    }

    private void SelectSavedConnection(object? sender, SelectionChangedEventArgs args)
    {
        if (SavedConnections.SelectedItem is not ComboBoxItem { Tag: EntityForgeConnectionProfile profile })
        {
            return;
        }

        _editingConnectionId = profile.Id;
        ConnectionNameInput.Text = profile.Name;
        ConnectionEndpointInput.Text = profile.Endpoint.AbsoluteUri;
        ConnectionTokenInput.Text = String.Empty;
        RemoveConnectionButton.IsEnabled = true;
    }

    private void NewConnectionProfile(object? sender, RoutedEventArgs args)
    {
        if (_profileEditPending)
        {
            return;
        }

        SavedConnections.SelectedItem = null;
        _editingConnectionId = Guid.NewGuid().ToString("N");
        ConnectionNameInput.Text = "New connection";
        ConnectionEndpointInput.Text = _defaultEndpoint.AbsoluteUri;
        ConnectionTokenInput.Text = String.Empty;
        RemoveConnectionButton.IsEnabled = false;
    }

    private async void SaveConnectionProfile(object? sender, RoutedEventArgs args)
    {
        if (_profileEditPending)
        {
            return;
        }

        _profileEditPending = true;
        try
        {
            var profile = new EntityForgeConnectionProfile(_editingConnectionId,
                ConnectionNameInput.Text ?? String.Empty, new Uri(ConnectionEndpointInput.Text ?? String.Empty, UriKind.Absolute)).Normalize();
            var profiles = await _connectionProfiles.SaveAsync(profile);
            if (ModalOverlay.IsVisible)
            {
                PopulateProfiles(profiles, profile.Id);
            }

            ConnectionInputError.IsVisible = false;
        }
        catch (Exception exception)
        {
            ConnectionInputError.Text = exception.Message;
            ConnectionInputError.IsVisible = true;
        }
        finally
        {
            _profileEditPending = false;
        }
    }

    private async void RemoveConnectionProfile(object? sender, RoutedEventArgs args)
    {
        if (_profileEditPending || SavedConnections.SelectedItem is not ComboBoxItem { Tag: EntityForgeConnectionProfile profile })
        {
            return;
        }

        _profileEditPending = true;
        try
        {
            var profiles = await _connectionProfiles.RemoveAsync(profile.Id);
            if (ModalOverlay.IsVisible)
            {
                PopulateProfiles(profiles, null);
            }

            _editingConnectionId = Guid.NewGuid().ToString("N");
            RememberConnectionInput.IsChecked = false;
            ConnectionInputError.Text = "Saved profile removed. The database is unchanged.";
            ConnectionInputError.IsVisible = true;
        }
        catch (Exception exception)
        {
            ConnectionInputError.Text = exception.Message;
            ConnectionInputError.IsVisible = true;
        }
        finally
        {
            _profileEditPending = false;
        }
    }

    private Task<ConnectionOverlayResult?> ShowConnectionOverlayAsync(EntityForgeConnectionProfile initial, IReadOnlyList<EntityForgeConnectionProfile> profiles,
        string? error = null, bool remember = true)
    {
        PopulateProfiles(profiles, initial.Id);
        _editingConnectionId = initial.Id;
        ConnectionNameInput.Text = initial.Name;
        ConnectionEndpointInput.Text = initial.Endpoint.AbsoluteUri;
        ConnectionTokenInput.Text = String.Empty;
        RememberConnectionInput.IsChecked = remember;
        ConnectionInputError.Text = error;
        ConnectionInputError.IsVisible = error is not null;
        ModalOverlay.IsVisible = true;
        if (error is null)
        {
            ConnectionEndpointInput.Focus();
        }
        else
        {
            ConnectionTokenInput.Focus();
        }

        _connectionOverlayCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return _connectionOverlayCompletion.Task;
    }

    private void AcceptConnectionOverlay(object? sender, RoutedEventArgs args)
    {
        if (_profileEditPending)
        {
            return;
        }

        if (String.IsNullOrWhiteSpace(ConnectionNameInput.Text) ||
            !Uri.TryCreate(ConnectionEndpointInput.Text?.Trim(), UriKind.Absolute, out Uri? endpoint) ||
            endpoint.Scheme is not ("http" or "https"))
        {
            ConnectionInputError.Text = "Enter a connection name and an absolute HTTP or HTTPS host address.";
            ConnectionInputError.IsVisible = true;
            return;
        }

        CompleteConnectionOverlay(new(_editingConnectionId, ConnectionNameInput.Text.Trim(), endpoint,
            String.IsNullOrWhiteSpace(ConnectionTokenInput.Text) ? null : ConnectionTokenInput.Text,
            RememberConnectionInput.IsChecked == true));
    }

    private void CancelConnectionOverlay(object? sender, RoutedEventArgs args) => CompleteConnectionOverlay(null);

    private void CompleteConnectionOverlay(ConnectionOverlayResult? result)
    {
        ModalOverlay.IsVisible = false;
        ConnectionTokenInput.Text = String.Empty;
        TaskCompletionSource<ConnectionOverlayResult?>? completion = _connectionOverlayCompletion;
        _connectionOverlayCompletion = null;
        completion?.TrySetResult(result);
        ConnectButton.Focus();
    }

    private void ShowDisconnected()
    {
        ConnectionNotice.IsVisible = false;
        WelcomePanel.IsVisible = true;
        Workspace.IsVisible = false;
        ConnectionStatus.Text = "Not connected";
        ConnectionLed.Fill = Brush(EntityForgeDesignTokens.Resource.Warning);
        ConnectButton.Content = "Connect";
    }

    private Task MessageAsync(string title, string message)
    {
        ConnectionStatus.Text = $"{title}: {message}";
        ConnectionLed.Fill = Brush(EntityForgeDesignTokens.Resource.Danger);
        return Task.CompletedTask;
    }

    // Error reporting must remain usable even if a theme resource is absent or invalid.
    private static IBrush Brush(string resource) => Application.Current?.FindResource(resource) as IBrush ?? Brushes.Gray;

    private sealed record ConnectionOverlayResult(string Id, string Name, Uri Endpoint, string? BearerToken, bool Remember);

    private void CaptureAndSave()
    {
        CaptureLayout();
        Save();
    }

    private void CaptureLayout()
    {
        if (Workspace.Layout is not null)
        {
            _session.UpdateSelectedLayout(Workspace.Layout, Workspace.ResponsivePreferences);
        }

        ConfigurePanels();
        _session.SetPanelConnections(_moduleViews.Presenter.Connections.Capture());
    }

    private async void Save()
    {
        await _persistence.SaveAsync(_session.Document);
        RenderPersistenceStatus();
    }

    private void RenderPersistenceStatus()
    {
        PersistenceNotice.IsVisible = _persistence.Error is not null;
        PersistenceMessage.Text = _persistence.Error;
        RetryPersistenceButton.Content = _persistence.LoadFailed ? "Retry load" : "Retry save";
        ReplaceLayoutButton.IsVisible = _persistence.LoadFailed;
    }

    private async void RetryPersistence(object? sender, RoutedEventArgs args)
    {
        if (_persistence.LoadFailed)
        {
            await LoadWorkspaceAsync();
        }
        else
        {
            CaptureLayout();
            await _persistence.SaveAsync(_session.Document, retry: true);
            RenderPersistenceStatus();
        }
    }

    private async void ReplaceUnreadableLayout(object? sender, RoutedEventArgs args)
    {
        CaptureLayout();
        await _persistence.SaveAsync(_session.Document, retry: true, replaceUnreadable: true);
        RenderPersistenceStatus();
    }
}
