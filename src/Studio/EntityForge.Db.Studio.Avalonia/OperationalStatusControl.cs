using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EntityForge.Db.AppModel.Connections;

namespace EntityForge.Db.Studio.Avalonia;

internal sealed class OperationalStatusControl : ContentControl
{
    private readonly EntityForgeConnectionSession _connection;

    public OperationalStatusControl(EntityForgeConnectionSession connection)
    {
        _connection = connection;
        AttachedToVisualTree += (_, _) =>
        {
            connection.OperationalStatusChanged += Changed;
            connection.Changed += Changed;
            Render();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            connection.OperationalStatusChanged -= Changed;
            connection.Changed -= Changed;
        };
        Render();
    }

    private void Changed(object? sender, EventArgs args) => Dispatcher.UIThread.Post(Render);

    private bool _detailsExpanded;

    private void Render()
    {
        var body = new StackPanel { Spacing = 14 };
        Content = body;
        if (_connection.Current is not { } current)
        {
            body.Children.Add(Card(new StackPanel
            {
                Spacing = 8,
                Children =
            {
                Label("Not connected", "ef-title"),
                Label("Connect to a database to view its health and activity.", "ef-muted")
            }
            }));
            if (_connection.Error is { } failure)
            {
                body.Children.Add(Notice("Connection problem", failure));
            }

            return;
        }

        var observation = _connection.OperationalStatus;
        var report = observation?.Report;
        bool stale = observation?.Error is not null;
        string health = observation is null ? "Checking status…"
            : !observation.Reachable ? "Host unreachable"
            : stale ? "Status unavailable"
            : report?.Ready == true ? "Ready" : "Not ready";
        string tone = observation is null ? "ef-muted"
            : !stale && report?.Ready == true ? "ef-success" : "ef-warning";
        var badges = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };
        badges.Children.Add(Badge(current.Capabilities.Profile.Identifier));
        if (observation is not null)
        {
            badges.Children.Add(Badge(observation.Reachable ? "Host reachable" : "No response"));
        }

        var summary = new StackPanel
        {
            Spacing = 8,
            Children =
        {
            Label(health, "ef-display", tone),
            Label(current.Profile.Name, "ef-section-title"),
            badges,
            Label(current.Profile.Endpoint.ToString(), "ef-muted")
        }
        };
        body.Children.Add(Card(summary));
        if (observation is null)
        {
            body.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 3 });
            return;
        }
        var freshness = Label($"Checked {observation.CheckedAt.LocalDateTime:T} · Auto-refresh", "ef-muted");
        ToolTip.SetTip(freshness, $"Checked {observation.CheckedAt.LocalDateTime:G}. Checks run about every 10 seconds; failed requests may take longer.");
        body.Children.Add(freshness);
        if (observation.Error is { } error)
        {
            body.Children.Add(Notice(report is null ? "Diagnostics unavailable" : "Showing last known values", error +
                (report is null ? "" : $" Last received {observation.ReceivedAt?.LocalDateTime:G}.")));
        }

        if (report is null)
        {
            return;
        }

        body.Children.Add(Label(stale ? "Last known snapshot" : "Database snapshot", "ef-section-title"));
        var metrics = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 8,
            RowSpacing = 8
        };
        AddMetric(metrics, Metric("Version", report.Version.ToString("N0"), "Database revision"), 0, 0);
        AddMetric(metrics, Metric("Recovery log", Bytes(report.WalBytes), "Write-ahead log"), 0, 1);
        if (report.ChangeJournal is { } journal)
        {
            AddMetric(metrics, Metric("Change journal", Bytes(journal.RetainedBytes), "Retained changes"), 1, 0);
            AddMetric(metrics, Metric("Segments", journal.SegmentCount.ToString("N0"), "Journal segments"), 1, 1);
        }
        body.Children.Add(metrics);
        body.Children.Add(Card(new StackPanel
        {
            Spacing = 6,
            Children =
        {
            Label("Maintenance", "ef-section-title"),
            Label(report.MaintenanceFailure is null ? "No failure reported" : "Needs attention",
                report.MaintenanceFailure is null ? "ef-muted" : "ef-warning"),
            Label(report.MaintenanceFailure ?? "In the latest received report.", "ef-muted")
        }
        }));
        if (report.ChangeJournal is { } changes && (changes.RetentionPressureBytes > 0 || changes.MaterializationDebt > 0))
        {
            body.Children.Add(Notice("Journal needs attention",
                $"Retention pressure: {Bytes(changes.RetentionPressureBytes)} · Materialization debt: {changes.MaterializationDebt:N0}."));
        }

        var details = new StackPanel { Spacing = 10 };
        details.Children.Add(Detail("Readiness", (report.Ready ? "Ready" : "Not ready") + (stale ? " (last known)" : "")));
        details.Children.Add(Detail("Received", $"{observation.ReceivedAt?.LocalDateTime:G}"));
        details.Children.Add(Detail("Database identity", report.DatabaseIncarnation.ToString()));
        if (report.ChangeJournal is { } diagnostics)
        {
            details.Children.Add(Detail("Retention pressure", Bytes(diagnostics.RetentionPressureBytes)));
            details.Children.Add(Detail("Materialization debt", diagnostics.MaterializationDebt.ToString("N0")));
            details.Children.Add(Detail("Retention floor", diagnostics.RetentionFloorVersion?.ToString("N0") ?? "Not reported"));
        }
        else
        {
            details.Children.Add(Label("Change-journal diagnostics are not reported by this host.", "ef-muted"));
        }

        details.Children.Add(Label("Recovery log size is not total database size. Use Storage Profile for filesystem capacity and I/O diagnostics. Archive inventory and cluster topology are not included in this report.", "ef-muted"));
        var expander = new Expander
        {
            Header = "Technical details",
            Content = details,
            IsExpanded = _detailsExpanded,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        expander.PropertyChanged += (_, args) =>
        {
            if (args.Property == Expander.IsExpandedProperty)
            {
                _detailsExpanded = expander.IsExpanded;
            }
        };
        body.Children.Add(expander);
    }

    private static TextBlock Label(string text, params string[] classes)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        foreach (string name in classes)
        {
            label.Classes.Add(name);
        }

        return label;
    }

    private static Border Card(Control content) => new() { Classes = { "ef-control-group" }, Child = content };

    private static Border Badge(string text) => new() { Classes = { "ef-badge" }, Child = Label(text) };

    private static Control Notice(string title, string message) => Card(new StackPanel
    {
        Spacing = 6,
        Children = { Label(title, "ef-section-title", "ef-warning"), Label(message, "ef-muted") }
    });

    private static Control Metric(string title, string value, string description) => Card(new StackPanel
    {
        Spacing = 5,
        Children =
        {
            Label(title, "ef-muted"), new TextBlock
        {
            Text = value, FontSize = 20,
            FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap
        }, Label(description, "ef-muted")
        }
    });

    private static Control Detail(string title, string value) => new StackPanel
    {
        Spacing = 3,
        Children = { Label(title, "ef-muted"), Label(value) }
    };

    private static void AddMetric(Grid grid, Control metric, int row, int column)
    {
        Grid.SetRow(metric, row);
        Grid.SetColumn(metric, column);
        grid.Children.Add(metric);
    }

    private static string Bytes(long bytes) => $"{bytes / 1048576d:N2} MiB";
}
