using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using EntityForge.Db.AppModel.Connections;

namespace EntityForge.Db.Studio.Avalonia;

internal sealed class HistoryControl : InspectionPanel
{
    public HistoryControl(EntityForgeConnectionSession connection)
    {
        var range = new ComboBox { ItemsSource = new[] { "15 minutes", "1 hour", "6 hours", "24 hours" }, SelectedIndex = 0 };
        var refresh = new Button { Content = "Refresh" };
        var auto = new CheckBox { Content = "Auto refresh" };
        var status = InspectionView.Text("");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        Header.Children.Add(new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { range, refresh, auto } });
        Header.Children.Add(status);
        CancellationTokenSource? request = null;
        int generation = 0;
        var cards = new Dictionary<string, HistoryMetricCard>();
        async Task Load()
        {
            request?.Cancel();
            request?.Dispose();
            request = new();
            var token = request.Token;
            int version = ++generation;
            refresh.IsEnabled = false;
            try
            {
                if (!connection.SupportsObservability)
                {
                    Body.Children.Clear();
                    status.Text = "Connect to a host with observability support.";
                    return;
                }
                if (connection.CanAdminister == false)
                {
                    Body.Children.Clear();
                    status.Text = "Administration access is required.";
                    return;
                }
                var config = await connection.ReadObservabilityConfigurationAsync(token);
                if (version != generation)
                {
                    return;
                }

                if (!config.History)
                {
                    Body.Children.Clear();
                    status.Text = "History is not configured. Live metrics are available in the Live metrics tab.";
                    Body.Children.Add(ObservabilitySetup.History());
                    return;
                }
                var report = await connection.ReadHistoryAsync(new[] { 15, 60, 360, 1440 }[range.SelectedIndex], token);
                if (version != generation)
                {
                    return;
                }

                status.Text = $"{report.Start.LocalDateTime:t}–{report.End.LocalDateTime:t} · {report.StepSeconds}s resolution";
                var visible = new List<Control>();
                foreach (var series in report.Series)
                {
                    if (!cards.TryGetValue(series.Name, out var card))
                    {
                        cards.Add(series.Name, card = new(series, report));
                    }
                    else
                    {
                        card.Update(series, report);
                    }

                    visible.Add(card.Card);
                }
                Reconcile(Body, visible);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or System.IO.InvalidDataException)
            {
                if (version == generation)
                {
                    status.Text = Body.Children.Count > 0 ? "History unavailable · displayed charts are stale" : "History unavailable. Check the server's Prometheus connection.";
                }
            }
            finally
            {
                if (version == generation)
                {
                    refresh.IsEnabled = true;
                }
            }
        }
        refresh.Click += async (_, _) => await Load();
        range.SelectionChanged += async (_, _) =>
        {
            cards.Clear();
            Body.Children.Clear();
            await Load();
        };
        timer.Tick += async (_, _) =>
        {
            if (auto.IsChecked == true && refresh.IsEnabled)
            {
                await Load();
            }
        };
        void Changed(object? sender, EventArgs args) => Dispatcher.UIThread.Post(async () =>
        {
            cards.Clear();
            Body.Children.Clear();
            await Load();
        });
        AttachedToVisualTree += async (_, _) =>
        {
            connection.Changed += Changed;
            timer.Start();
            await Load();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            connection.Changed -= Changed;
            timer.Stop();
            generation++;
            request?.Cancel();
        };
    }
}

internal sealed class HistoryMetricCard
{
    public Border Card
    {
        get;
    }

    private readonly HistoryChart _chart;
    private readonly TextBlock _summary = InspectionView.Text("");
    private readonly StackPanel _samples = new() { Spacing = 3 };
    private readonly Expander _details;
    private HistorySeries _series;

    public HistoryMetricCard(HistorySeries series, HistoryReport report)
    {
        _series = series;
        _chart = new(series, report)
        {
            Height = 120
        };
        _details = new Expander { Header = "Samples", Content = _samples };
        _details.PropertyChanged += (_, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty && _details.IsExpanded)
            {
                Samples();
            }
        };
        Card = InspectionView.Card(new StackPanel
        {
            Spacing = 6,
            Children =
            { InspectionView.Text(series.Name, "ef-section-title"), _summary, _chart, _details }
        });
        Update(series, report);
    }

    public void Update(HistorySeries series, HistoryReport report)
    {
        _series = series;
        _chart.Update(series, report);
        var values = series.Points.Where(p => p.Value.HasValue).ToArray();
        _summary.Text = values.Length == 0 ? "No samples in this range" : $"Latest {values[^1].Value:N2} · Max {values.Max(v => v.Value):N2} {series.Unit}";
        _details.Header = $"Samples · {values.Length}";
        if (_details.IsExpanded)
        {
            Samples();
        }
    }

    private void Samples()
    {
        _samples.Children.Clear();
        foreach (var point in _series.Points)
        {
            _samples.Children.Add(InspectionView.Text($"{DateTimeOffset.FromUnixTimeSeconds((long)point.Time).LocalDateTime:T}   {(point.Value is { } v ? v.ToString("N2") : "—")} {_series.Unit}"));
        }
    }
}

internal sealed class HistoryChart(HistorySeries series, HistoryReport report) : Control
{
    internal void Update(HistorySeries nextSeries, HistoryReport nextReport)
    {
        series = nextSeries;
        report = nextReport;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double max = Math.Max(1e-9, series.Points.Where(p => p.Value.HasValue).Select(p => p.Value!.Value).DefaultIfEmpty(1).Max());
        var pen = new Pen(Brushes.SteelBlue, 2);
        double left = report.Start.ToUnixTimeSeconds(), width = Math.Max(1, (report.End - report.Start).TotalSeconds);
        Point? previous = null;
        double previousTime = 0;
        context.DrawLine(new Pen(Brushes.Gray, 1), new(0, Bounds.Height - 1), new(Bounds.Width, Bounds.Height - 1));
        foreach (var sample in series.Points)
        {
            if (sample.Value is not { } value || !double.IsFinite(value))
            {
                previous = null;
                continue;
            }
            var point = new Point(Math.Clamp((sample.Time - left) / width, 0, 1) * Bounds.Width,
                (1 - Math.Clamp(value / max, 0, 1)) * (Bounds.Height - 8) + 3);
            if (previous is { } from && sample.Time - previousTime <= report.StepSeconds * 1.5)
            {
                context.DrawLine(pen, from, point);
            }
            else
            {
                context.DrawEllipse(Brushes.SteelBlue, null, point, 2, 2);
            }

            previous = point;
            previousTime = sample.Time;
        }
    }
}

internal sealed class QueryProfilerControl : InspectionPanel
{
    public QueryProfilerControl(EntityForgeConnectionSession connection)
    {
        var range = new ComboBox { ItemsSource = new[] { "15 minutes", "1 hour", "6 hours", "24 hours" }, SelectedIndex = 0 };
        var minimum = new ComboBox { ItemsSource = new[] { "All durations", "At least 10 ms", "At least 100 ms", "At least 1 s" }, SelectedIndex = 0 };
        var order = new ComboBox { ItemsSource = new[] { "Newest first", "Slowest first" }, SelectedIndex = 0 };
        var refresh = new Button { Content = "Search" };
        var back = new Button { Content = "Back to traces", IsVisible = false };
        var previous = new Button { Content = "Previous" };
        var next = new Button { Content = "Next" };
        var status = InspectionView.Text("");
        var count = InspectionView.Text("");
        var paging = new WrapPanel { ItemSpacing = 8, Children = { previous, next, count } };
        var toolbar = new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { range, minimum, order, refresh, back } };
        Header.Children.Add(toolbar);
        Header.Children.Add(paging);
        Header.Children.Add(status);
        ProfileSearch? results = null;
        int page = 0, generation = 0;
        CancellationTokenSource? request = null;
        CancellationToken Begin()
        {
            request?.Cancel();
            request?.Dispose();
            request = new();
            generation++;
            return request.Token;
        }
        void List()
        {
            back.IsVisible = false;
            paging.IsVisible = true;
            Body.Children.Clear();
            var rows = order.SelectedIndex == 1 ? results?.Traces.OrderByDescending(t => t.DurationMs).ToArray() : results?.Traces;
            page = Math.Clamp(page, 0, Math.Max(0, ((rows?.Length ?? 0) - 1) / 20));
            count.Text = rows is not { Length: > 0 } ? "No traces" : $"{page * 20 + 1}–{Math.Min(page * 20 + 20, rows.Length)} of {rows.Length}";
            previous.IsEnabled = page > 0;
            next.IsEnabled = (page + 1) * 20 < (rows?.Length ?? 0);
            foreach (var trace in (rows ?? []).Skip(page * 20).Take(20))
            {
                var open = new Button { Content = new TextBlock { Text = $"{trace.StartedAt.LocalDateTime:T} · {trace.DurationMs:N2} ms\n{trace.Route ?? "Database query"} · {trace.Outcome ?? "Captured"}", TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch };
                ToolTip.SetTip(open, trace.Id);
                open.Click += async (_, _) => await Detail(trace);
                Body.Children.Add(open);
            }
        }
        async Task Search()
        {
            var token = Begin();
            int version = generation;
            refresh.IsEnabled = false;
            try
            {
                if (!connection.SupportsObservability)
                {
                    results = null;
                    List();
                    status.Text = "Connect to a host with observability support.";
                    return;
                }
                if (connection.CanAdminister == false)
                {
                    results = null;
                    List();
                    status.Text = "Administration access is required.";
                    return;
                }
                var config = await connection.ReadObservabilityConfigurationAsync(token);
                if (version != generation)
                {
                    return;
                }

                if (!config.Profiler)
                {
                    results = null;
                    List();
                    paging.IsVisible = false;
                    status.Text = "Query trace capture is not configured.";
                    Body.Children.Add(ObservabilitySetup.Profiler());
                    return;
                }
                var found = await connection.FindTracesAsync(new[] { 15, 60, 360, 1440 }[range.SelectedIndex], token, new[] { 0, 10, 100, 1000 }[minimum.SelectedIndex]);
                if (version != generation)
                {
                    return;
                }

                results = found;
                page = 0;
                List();
                status.Text = $"{found.Start.LocalDateTime:t}–{found.End.LocalDateTime:t} · Captured traces" + (found.LimitReached ? " · 100-result limit; narrow the range" : "");
                ToolTip.SetTip(status, "Trace duration includes the whole distributed trace. Export delay, retention and sampling can omit requests. The returned matches are not guaranteed to be the newest 100.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or System.IO.InvalidDataException)
            {
                if (version == generation)
                {
                    status.Text = "Trace search unavailable" + (results is null ? ". Check the server's Tempo connection." : " · displayed matches are stale");
                }
            }
            finally
            {
                if (version == generation)
                {
                    refresh.IsEnabled = true;
                }
            }
        }
        async Task Detail(ProfileTrace trace)
        {
            var token = Begin();
            int version = generation;
            refresh.IsEnabled = false;
            try
            {
                var detail = await connection.ReadTraceAsync(trace.Id, token);
                if (version != generation)
                {
                    return;
                }

                back.IsVisible = true;
                paging.IsVisible = false;
                Body.Children.Clear();
                Viewport.Offset = default;
                status.Text = detail.Truncated ? "First 500 spans · truncated" : $"{detail.Spans.Length} database spans";
                Body.Children.Add(new SelectableTextBlock { Text = detail.Id });
                double total = Math.Max(0.001, detail.Spans.Select(s => s.OffsetMs + s.DurationMs).DefaultIfEmpty(0).Max());
                foreach (var span in detail.Spans)
                {
                    var text = $"{span.Name} · {span.DurationMs:N2} ms";
                    var info = new StackPanel
                    {
                        Spacing = 5,
                        Children =
                    {
                        InspectionView.Text(text), new SpanTimeline(span.OffsetMs, span.DurationMs, total) { Height = 12 },
                        new Expander
                        {
                            Header = "Details", Content = new StackPanel
                        {
                            Spacing = 4, Children =
                        {
                            new SelectableTextBlock { Text = $"Span {span.Id}\nParent {span.ParentId}" },
                            InspectionView.Text($"Start +{span.OffsetMs:N2} ms · {span.Outcome ?? "internal stage"}"),
                            InspectionView.Text(span.Route ?? "")
                        }
                        }
                        }
                    }
                    };
                    Body.Children.Add(InspectionView.Card(info));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or System.IO.InvalidDataException)
            {
                if (version == generation)
                {
                    status.Text = "Trace unavailable. It may have expired or still be exporting.";
                }
            }
            finally
            {
                if (version == generation)
                {
                    refresh.IsEnabled = true;
                }
            }
        }
        refresh.Click += async (_, _) => await Search();
        back.Click += (_, _) =>
        {
            Begin();
            refresh.IsEnabled = true;
            List();
            status.Text = results?.LimitReached == true ? "100-result limit · narrow the range" : "Captured traces";
        };
        range.SelectionChanged += async (_, _) =>
        {
            results = null;
            List();
            await Search();
        };
        minimum.SelectionChanged += async (_, _) =>
        {
            results = null;
            List();
            await Search();
        };
        order.SelectionChanged += (_, _) =>
        {
            page = 0;
            if (!back.IsVisible)
            {
                List();
            }
        };
        previous.Click += (_, _) =>
        {
            page--;
            List();
            Viewport.Offset = default;
        };
        next.Click += (_, _) =>
        {
            page++;
            List();
            Viewport.Offset = default;
        };
        void Changed(object? sender, EventArgs args) => Dispatcher.UIThread.Post(async () =>
        {
            results = null;
            List();
            await Search();
        });
        AttachedToVisualTree += async (_, _) =>
        {
            connection.Changed += Changed;
            await Search();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            connection.Changed -= Changed;
            generation++;
            request?.Cancel();
        };
    }
}

internal sealed class SpanTimeline(double offset, double duration, double total) : Control
{
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Brushes.SteelBlue, null, new Rect(offset / total * Bounds.Width, 0,
            Math.Max(2, duration / total * Bounds.Width), Bounds.Height));
    }
}
