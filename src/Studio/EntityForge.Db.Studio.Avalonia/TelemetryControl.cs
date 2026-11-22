using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using EntityForge.Db.AppModel.Connections;

namespace EntityForge.Db.Studio.Avalonia;

internal sealed class TelemetryControl : InspectionPanel
{
    public TelemetryControl(EntityForgeConnectionSession connection)
    {
        int page = 0;
        bool paused = false, refreshing = false;
        Guid? session = null;
        var rowsByKey = new Dictionary<(string, string, string, string?), MetricRow>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        var summary = InspectionView.Text("");
        var notice = InspectionView.Text("", "ef-warning");
        var details = InspectionView.Text("");
        var filter = new TextBox { PlaceholderText = "Filter metrics or meters" };
        var refresh = new Button { Content = "Refresh now" };
        var pause = new Button { Content = "Pause live updates" };
        var previous = new Button { Content = "Previous" };
        var next = new Button { Content = "Next" };
        var count = InspectionView.Text("");
        var metrics = new StackPanel { Spacing = 8 };
        Header.Children.Add(new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { refresh, pause, summary } });
        Header.Children.Add(filter);
        Header.Children.Add(new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { previous, next, count } });
        Header.Children.Add(notice);
        Body.Children.Add(metrics);
        Body.Children.Add(new Expander
        {
            Header = "Collection details",
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
        {
            details, InspectionView.Text("Process-local database instruments. Values aggregate all tags and databases in this process since monitoring began. OTLP export is independent; retained history and traces are not shown here.")
        }
            }
        });
        global::Avalonia.Automation.AutomationProperties.SetName(filter, "Filter telemetry metrics");
        void Render()
        {
            notice.Text = "";
            notice.IsVisible = false;
            bool available = connection.SupportsLiveTelemetry && connection.CanAdminister != false;
            refresh.IsEnabled = available && !refreshing;
            pause.IsEnabled = available;
            pause.Content = paused ? "Resume live updates" : "Pause live updates";
            var observation = connection.Telemetry;
            var report = observation?.Report;
            filter.IsVisible = previous.IsVisible = next.IsVisible = count.IsVisible = available && report is not null;
            if (!available)
            {
                summary.Text = connection.Current is null ? "Connect to view metrics."
                    : connection.CanAdminister == false ? "This credential does not have administration access to telemetry."
                    : "Live metrics are unavailable on this host.";
                Body.IsVisible = false;
                return;
            }
            if (observation?.Error is { } error)
            {
                notice.Text = error;
                notice.IsVisible = true;
            }
            Body.IsVisible = report is not null;
            if (report is null)
            {
                summary.Text = refreshing ? "Reading metrics…" : "Waiting for metrics.";
                return;
            }
            if (session != report.ProcessSession)
            {
                session = report.ProcessSession;
                rowsByKey.Clear();
                Viewport.Offset = default;
            }
            summary.Text = $"Process · {(paused ? "Paused" : "Live · 5s")}" + (observation?.Error is not null ? " · Stale" : "");
            ToolTip.SetTip(summary, $"Monitoring began {report.StartedAt.LocalDateTime:G}. Captured {report.CapturedAt.LocalDateTime:G}.");
            details.Text = $"Monitoring began {report.StartedAt.LocalDateTime:G}\nCaptured {report.CapturedAt.LocalDateTime:G}\n{report.Metrics.Count} of {report.MaximumMetrics} metric identities · {report.DroppedMeasurements:N0} measurements omitted";
            if (report.DroppedMeasurements > 0)
            {
                notice.Text += $" {report.DroppedMeasurements:N0} measurements omitted; see collection details.";
                notice.IsVisible = true;
            }
            string query = filter.Text?.Trim() ?? "";
            var rows = report.Metrics.Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || m.Meter.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            page = Math.Clamp(page, 0, Math.Max(0, (rows.Length - 1) / 20));
            previous.IsEnabled = page > 0;
            next.IsEnabled = (page + 1) * 20 < rows.Length;
            count.Text = rows.Length == 0 ? (report.Metrics.Count == 0 ? "No measurements yet" : "No matching metrics") : $"{page * 20 + 1}–{Math.Min((page + 1) * 20, rows.Length)} of {rows.Length}";
            var currentKeys = report.Metrics.Select(m => (m.Meter, m.Name, m.Kind, m.Unit)).ToHashSet();
            foreach (var key in rowsByKey.Keys.Where(k => !currentKeys.Contains(k)).ToArray())
            {
                rowsByKey.Remove(key);
            }

            var visible = new List<Control>();
            foreach (var metric in rows.Skip(page * 20).Take(20))
            {
                var key = (metric.Meter, metric.Name, metric.Kind, metric.Unit);
                if (!rowsByKey.TryGetValue(key, out var row))
                {
                    rowsByKey.Add(key, row = new MetricRow(metric));
                }

                row.Update(metric);
                visible.Add(row.Card);
            }
            Reconcile(metrics, visible);
        }
        async Task Refresh()
        {
            if (refreshing || !connection.SupportsLiveTelemetry || connection.CanAdminister == false)
            {
                return;
            }

            refreshing = true;
            Render();
            try
            {
                await connection.RefreshTelemetryAsync();
            }
            finally
            {
                refreshing = false;
                Render();
            }
        }
        timer.Tick += async (_, _) => await Refresh();
        refresh.Click += async (_, _) => await Refresh();
        pause.Click += (_, _) =>
        {
            paused = !paused;
            if (paused)
            {
                timer.Stop();
            }
            else
            {
                timer.Start();
            }
            Render();
        };
        filter.TextChanged += (_, _) =>
        {
            page = 0;
            Viewport.Offset = default;
            Render();
        };
        previous.Click += (_, _) =>
        {
            page--;
            Viewport.Offset = default;
            Render();
        };
        next.Click += (_, _) =>
        {
            page++;
            Viewport.Offset = default;
            Render();
        };
        void Changed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Render);
        AttachedToVisualTree += async (_, _) =>
        {
            connection.TelemetryChanged += Changed;
            connection.Changed += Changed;
            connection.AccessChanged += Changed;
            if (!paused)
            {
                timer.Start();
            }
            await Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop();
            connection.TelemetryChanged -= Changed;
            connection.Changed -= Changed;
            connection.AccessChanged -= Changed;
        };
        Render();
    }

    private sealed class MetricRow
    {
        public Border Card
        {
            get;
        }

        private readonly TextBlock _value = InspectionView.Text("", "ef-section-title");
        private readonly TextBlock _statistics = InspectionView.Text("");
        private readonly TextBlock _time = InspectionView.Text("");

        public MetricRow(LiveMetric metric)
        {
            var title = new SelectableTextBlock { Text = metric.Name, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
            Card = InspectionView.Card(new StackPanel
            {
                Spacing = 6,
                Children =
            {
                title, _value, new Expander
                {
                    Header = "Metric details", Content = new StackPanel
                {
                    Spacing = 5, Children =
                    { InspectionView.Text(metric.Meter), _statistics, _time }
                }
                }
            }
            });
        }

        public void Update(LiveMetric metric)
        {
            string Unit(double value) => $"{value:N2}{(string.IsNullOrEmpty(metric.Unit) ? "" : " " + metric.Unit)}";
            _value.Text = metric.Kind == "Histogram" ? $"Mean {Unit(metric.Sum / metric.Samples)} · {metric.Samples:N0} samples"
                : $"{(metric.Kind == "UpDownCounter" ? "Net change" : "Total")} {Unit(metric.Sum)}";
            _statistics.Text = $"Min {Unit(metric.Minimum)} · Max {Unit(metric.Maximum)} · Last {Unit(metric.Last)}";
            _statistics.IsVisible = metric.Kind == "Histogram";
            _time.Text = $"Last measurement {metric.LastObservedAt.LocalDateTime:G}";
        }
    }
}
