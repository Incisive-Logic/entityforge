using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using EntityForge.Db.AppModel.Connections;
using EntityForge.Db.AppModel.Optimizations;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Studio.Avalonia;

internal sealed class SemanticOptimizerControl : InspectionPanel
{
    private const int PageSize = 20;

    public SemanticOptimizerControl(EntityForgeConnectionSession connection, SemanticPropertyOptimizationSession optimizer)
    {
        int page = 0;
        var cached = new Dictionary<(string, bool), PropertyRow>();
        var summary = InspectionView.Text("");
        var notice = InspectionView.Text("", "ef-warning");
        var filter = new TextBox { PlaceholderText = "Filter by property identifier or recommendation reason" };
        var mode = new ComboBox { ItemsSource = new[] { "Recommendations", "All observations" }, SelectedIndex = 0 };
        var refresh = new Button { Content = "Analyse workload" };
        var cancel = new Button { Content = "Stop analysis" };
        var previous = new Button { Content = "Previous" };
        var next = new Button { Content = "Next" };
        var count = InspectionView.Text("");
        var entries = new StackPanel { Spacing = 8 };
        Header.Children.Add(new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { mode, refresh, cancel } });
        Header.Children.Add(filter);
        Header.Children.Add(new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { previous, next, count } });
        Header.Children.Add(summary);
        Header.Children.Add(notice);
        Body.Children.Add(new Expander { Header = "Optimization actions", Content = new AdministrationControl(connection, optimization: true) });
        Body.Children.Add(entries);
        global::Avalonia.Automation.AutomationProperties.SetName(filter, "Filter optimizer properties");
        global::Avalonia.Automation.AutomationProperties.SetName(mode, "Optimizer evidence view");
        void Render()
        {
            notice.Text = "";
            notice.IsVisible = false;
            bool supported = connection.SupportsOptimization && connection.CanAdminister != false;
            var report = optimizer.Report;
            refresh.IsEnabled = supported && optimizer.CanRun;
            refresh.Content = report is null ? "Analyse workload" : "Refresh evidence";
            cancel.IsVisible = optimizer.State == SemanticPropertyOptimizationState.Loading;
            filter.IsVisible = mode.IsVisible = report is not null && supported;
            previous.IsVisible = next.IsVisible = count.IsVisible = false;
            if (!supported)
            {
                summary.Text = connection.Current is null ? "Connect to inspect optimization evidence."
                    : connection.CanAdminister == false ? "This credential does not have administration access."
                    : "Optimization requires a ready Semantic host.";
                Body.IsVisible = false;
                cached.Clear();
                entries.Children.Clear();
                return;
            }
            Body.IsVisible = true;
            if (optimizer.Error is { } error)
            {
                notice.Text = error;
                notice.IsVisible = true;
            }
            if (report is null)
            {
                summary.Text = optimizer.State == SemanticPropertyOptimizationState.Loading ? "Analysing workload…" : "Analyse workload to see recommendations.";
                entries.Children.Clear();
                cached.Clear();
                return;
            }
            summary.Text = $"{report.Recommendations.Count:N0} recommendations · {report.ObservedProperties:N0} observed properties" +
                (optimizer.State == SemanticPropertyOptimizationState.Loading ? " · Refreshing…" : optimizer.State is SemanticPropertyOptimizationState.Failed or SemanticPropertyOptimizationState.Cancelled ? " · Stale" : "");
            ToolTip.SetTip(summary, $"Analysed {report.AnalyzedAt.LocalDateTime:G}. Tracking capacity: {report.MaximumObservedProperties:N0}. Recommendations depend on the server's evidence thresholds.");
            if (report.ObservationCapacityReached || report.DroppedPropertyObservations > 0)
            {
                notice.Text += $" Tracking capacity reached; {report.DroppedPropertyObservations:N0} observations dropped.";
                notice.IsVisible = true;
            }
            string query = filter.Text?.Trim() ?? "";
            bool Match(string value) => value.Contains(query, StringComparison.OrdinalIgnoreCase);
            var recommendations = report.Recommendations.Where(r => Match(r.Property.Identifier.AbsoluteUri) || Match(r.Reason)).ToArray();
            var observations = report.Observations.Where(o => Match(o.Property.Identifier.AbsoluteUri)).ToArray();
            int total = mode.SelectedIndex == 0 ? recommendations.Length : observations.Length;
            page = Math.Clamp(page, 0, Math.Max(0, (total - 1) / PageSize));
            previous.IsVisible = next.IsVisible = count.IsVisible = true;
            previous.IsEnabled = page > 0;
            next.IsEnabled = (page + 1) * PageSize < total;
            count.Text = total == 0 ? "No matches" : $"{page * PageSize + 1}–{Math.Min((page + 1) * PageSize, total)} of {total:N0}";
            var retained = report.Observations.Select(o => (o.Property.Identifier.AbsoluteUri, false))
                .Concat(report.Recommendations.Select(r => (r.Property.Identifier.AbsoluteUri, true))).ToHashSet();
            foreach (var key in cached.Keys.Where(k => !retained.Contains(k)).ToArray())
            {
                cached.Remove(key);
            }

            var visible = new List<Control>();
            if (mode.SelectedIndex == 0)
            {
                foreach (var recommendation in recommendations.Skip(page * PageSize).Take(PageSize))
                {
                    var key = (recommendation.Property.Identifier.AbsoluteUri, true);
                    if (!cached.TryGetValue(key, out var row))
                    {
                        cached.Add(key, row = new PropertyRow(key.Item1, true));
                    }

                    row.Update(recommendation.Evidence, recommendation);
                    visible.Add(row.Card);
                }
            }
            else
            {
                foreach (var observation in observations.Skip(page * PageSize).Take(PageSize))
                {
                    var key = (observation.Property.Identifier.AbsoluteUri, false);
                    if (!cached.TryGetValue(key, out var row))
                    {
                        cached.Add(key, row = new PropertyRow(key.Item1, false));
                    }

                    row.Update(observation, null);
                    visible.Add(row.Card);
                }
            }

            Reconcile(entries, visible);
        }
        filter.TextChanged += (_, _) =>
        {
            page = 0;
            Viewport.Offset = default;
            Render();
        };
        mode.SelectionChanged += (_, _) =>
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
        refresh.Click += async (_, _) => await optimizer.RefreshAsync();
        cancel.Click += (_, _) => optimizer.Cancel();
        void Changed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Render);
        AttachedToVisualTree += (_, _) =>
        {
            optimizer.Changed += Changed;
            connection.Changed += Changed;
            connection.AccessChanged += Changed;
            Render();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            optimizer.Changed -= Changed;
            connection.Changed -= Changed;
            connection.AccessChanged -= Changed;
        };
        Render();
    }

    private sealed class PropertyRow
    {
        public Border Card
        {
            get;
        }

        private readonly TextBlock _placement = InspectionView.Text("", "ef-section-title");
        private readonly TextBlock _reason = InspectionView.Text("");
        private readonly ContentControl _evidence = new();
        private SemanticPropertyWorkloadObservation? _last;

        public PropertyRow(string identifier, bool recommendation)
        {
            var body = new StackPanel { Spacing = 6, Children = { Property(identifier) } };
            if (recommendation)
            {
                body.Children.Add(_placement);
                body.Children.Add(_reason);
                body.Children.Add(new Expander { Header = "Workload evidence", Content = _evidence });
            }
            else
            {
                body.Children.Add(_evidence);
            }

            Card = InspectionView.Card(body);
        }

        public void Update(SemanticPropertyWorkloadObservation evidence, SemanticPropertyOptimizationRecommendation? recommendation)
        {
            if (recommendation is not null)
            {
                _placement.Text = $"{recommendation.CurrentStorageKind} → {recommendation.RecommendedStorageKind} · {recommendation.Confidence:P0} confidence";
                _reason.Text = recommendation.Reason;
            }
            if (_last != evidence)
            {
                _last = evidence;
                _evidence.Content = Evidence(evidence);
            }
        }
    }

    private static Control Property(string identifier) => new SelectableTextBlock { Text = identifier, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };

    private static Control Evidence(SemanticPropertyWorkloadObservation evidence) => new StackPanel
    {
        Spacing = 5,
        Children =
    {
        InspectionView.Text($"Equality queries: {evidence.EqualityQueries:N0} · Range queries: {evidence.RangeQueries:N0} · Writes: {evidence.Writes:N0}"),
        InspectionView.Text($"Candidates: {evidence.CandidateCount:N0} · Results: {evidence.ResultCount:N0} · Total query time: {evidence.QueryDuration.TotalMilliseconds:N1} ms"),
        InspectionView.Text($"Activity: {evidence.FirstActivityObservedAt.LocalDateTime:G} – {evidence.LastActivityObservedAt.LocalDateTime:G}"),
        InspectionView.Text(evidence.FirstQueryObservedAt is { } first && evidence.LastQueryObservedAt is { } last
            ? $"Query window: {first.LocalDateTime:G} – {last.LocalDateTime:G}" : "No query observation window recorded")
    }
    };
}
