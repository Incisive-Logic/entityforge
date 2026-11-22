using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EntityForge.Db.AppModel.Connections;

namespace EntityForge.Db.Studio.Avalonia;

internal static class InspectionView
{
    public static TextBlock Text(string text, string style = "ef-muted") => new() { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { style } };

    public static Border Card(Control content) => new() { Child = content, Padding = new Thickness(14), CornerRadius = new CornerRadius(8), Classes = { "ef-list-row" } };

    public static Control Metric(string title, string value) => Card(new StackPanel { Spacing = 6, Children = { Text(title), Text(value, "ef-section-title") } });

    public static Control Field(string label, string value) => new StackPanel
    {
        Spacing = 3,
        Children =
        { Text(label), new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap } }
    };
}

internal sealed class AccessControl : ContentControl
{
    private bool _refreshing;

    public AccessControl(EntityForgeConnectionSession connection)
    {
        void Changed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Render);
        AttachedToVisualTree += (_, _) =>
        {
            connection.AccessChanged += Changed;
            connection.Changed += Changed;
            Render();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            connection.AccessChanged -= Changed;
            connection.Changed -= Changed;
        };
        void Render()
        {
            var body = new StackPanel { Spacing = 12 };
            Content = body;
            if (connection.Current is not { } current)
            {
                body.Children.Add(InspectionView.Text("Connect to a database to inspect access."));
                return;
            }
            var observation = connection.Access;
            var report = observation?.Report;
            bool stale = observation?.Error is not null;
            string role = report?.Role switch
            {
                "Administration" => "Administrator",
                "Write" => "Writer",
                "Read" => "Reader",
                _ => "Not reported"
            };
            body.Children.Add(InspectionView.Card(new StackPanel
            {
                Spacing = 6,
                Children =
            {
                InspectionView.Text(stale ? "Access unavailable" : role, "ef-title"),
                InspectionView.Text(current.Profile.Name),
                InspectionView.Text(report?.Authentication == "bearer" ? "Bearer credential" : "Identity not reported")
            }
            }));
            string Permission(bool? allowed) => stale || report is null ? "Unknown" : allowed switch
            {
                true => "Allowed",
                false => "Denied",
                _ => "Unknown"
            };
            var permissions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
            permissions.Children.Add(InspectionView.Metric("Read data", Permission(report?.CanRead)));
            permissions.Children.Add(InspectionView.Metric("Write data", Permission(report?.CanWrite)));
            permissions.Children.Add(InspectionView.Metric("Administration", Permission(report?.CanAdminister)));
            permissions.Children.Add(InspectionView.Metric("Transport", current.Profile.Endpoint.Scheme == "https" ? "HTTPS" : "HTTP · unencrypted"));
            body.Children.Add(permissions);
            ToolTip.SetTip(permissions, "Current credential permissions. Resource-specific policy also applies; the server authorizes each request.");
            if (observation?.Error is { } error)
            {
                body.Children.Add(InspectionView.Text(error, "ef-warning"));
            }

            if (observation is not null)
            {
                body.Children.Add(InspectionView.Text($"Checked {observation.CheckedAt.LocalDateTime:G}"));
            }

            var refresh = new Button { Content = _refreshing ? "Checking…" : "Refresh Access", IsEnabled = !_refreshing };
            refresh.Click += async (_, _) =>
            {
                _refreshing = true;
                Render();
                try
                {
                    await connection.RefreshAccessAsync();
                }
                finally
                {
                    _refreshing = false;
                    Render();
                }
            };
            body.Children.Add(refresh);
        }
        Render();
    }
}

internal sealed class OperationLookupControl : StackPanel
{
    public OperationLookupControl(EntityForgeConnectionSession connection)
    {
        Spacing = 12;
        var operation = new TextBox { PlaceholderText = "Operation ID (GUID)", Text = connection.Operation?.OperationId.ToString("D") };
        var incarnation = new TextBox
        {
            PlaceholderText = "Database incarnation (GUID)",
            Text = connection.Current?.Capabilities.DatabaseIncarnation?.ToString("D") ?? connection.Operation?.DatabaseIncarnation.ToString("D"),
            IsReadOnly = connection.Current?.Capabilities.DatabaseIncarnation is not null
        };
        global::Avalonia.Automation.AutomationProperties.SetName(operation, "Operation ID");
        global::Avalonia.Automation.AutomationProperties.SetName(incarnation, "Database incarnation");
        var lookup = new Button { Content = "Look Up" };
        var cancel = new Button { Content = "Cancel" };
        var validation = InspectionView.Text("", "ef-warning");
        var result = new StackPanel { Spacing = 10 };
        ToolTip.SetTip(operation, "Application operation ID. Administration job IDs are looked up in Storage & Maintenance.");
        Children.Add(InspectionView.Text("Operation ID"));
        Children.Add(operation);
        Children.Add(new Expander { Header = "Database identity", IsExpanded = !incarnation.IsReadOnly, Content = incarnation });
        Children.Add(new WrapPanel { ItemSpacing = 8, Children = { lookup, cancel } });
        Children.Add(validation);
        Children.Add(result);
        void Render()
        {
            lookup.IsEnabled = connection.Current is not null && connection.SupportsOperationLookup && !connection.IsLookingUpOperation;
            cancel.IsVisible = connection.IsLookingUpOperation;
            result.Children.Clear();
            if (connection.Current is null)
            {
                result.Children.Add(InspectionView.Text("Connect to a database first."));
                return;
            }
            if (!connection.SupportsOperationLookup)
            {
                result.Children.Add(InspectionView.Text("This host does not advertise application-operation lookup. Upgrade the host or connect to a supported application gateway."));
                return;
            }
            if (connection.IsLookingUpOperation)
            {
                result.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 3 });
                return;
            }
            if (connection.Operation is not { } observation)
            {
                return;
            }

            result.Children.Add(InspectionView.Text($"Operation {observation.OperationId:D} · checked {observation.CheckedAt.LocalDateTime:G}"));
            if (observation.Error is { } error)
            {
                result.Children.Add(InspectionView.Text(error, "ef-warning"));
                return;
            }
            if (observation.Report is not { } report)
            {
                return;
            }

            string title = report.State switch
            {
                "NotFound" => "Not found · outcome unknown",
                "PlanBound" => "Plan bound · outcome pending",
                "CommittedPendingReadability" => "Committed · readability pending",
                _ => report.State
            };
            result.Children.Add(InspectionView.Card(new StackPanel
            {
                Spacing = 8,
                Children =
            {
                InspectionView.Text(title, "ef-title"), InspectionView.Text(OperationOutcome.Explanation(report.State))
            }
            }));
            if (report.Receipt is { } receipt)
            {
                result.Children.Add(NativeResultInspector.Create("Commit receipt", receipt));
            }
        }
        void Changed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Render);
        AttachedToVisualTree += (_, _) =>
        {
            connection.OperationChanged += Changed;
            connection.Changed += Changed;
            Render();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            connection.OperationChanged -= Changed;
            connection.Changed -= Changed;
        };
        lookup.Click += async (_, _) =>
        {
            validation.Text = "";
            if (!Guid.TryParse(operation.Text, out var id) || id == Guid.Empty || !Guid.TryParse(incarnation.Text, out var database) || database == Guid.Empty)
            {
                validation.Text = "Enter valid operation and database-incarnation GUIDs.";
                return;
            }
            try
            {
                await connection.LookupOperationAsync(id, database);
            }
            catch (Exception ex)
            {
                validation.Text = ex.Message;
            }
        };
        cancel.Click += (_, _) => connection.CancelOperationLookup();
        Render();
    }
}
