using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EntityForge.Db.AppModel.Queries;

namespace EntityForge.Db.Studio.Avalonia;

internal sealed class NativeCatalogueControl : StackPanel
{
    public NativeCatalogueControl(NativeCatalogueSession session, Action<string>? browse)
    {
        Spacing = 8;
        var search = new TextBox { PlaceholderText = "Filter discovered vocabulary" };
        var refresh = new Button { Content = "Discover vocabulary" };
        var cancel = new Button { Content = "Cancel" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var rows = new StackPanel { Spacing = 6 };
        Children.Add(new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { refresh, cancel } });
        Children.Add(search);
        Children.Add(status);
        Children.Add(rows);
        void Render()
        {
            refresh.IsEnabled = !session.IsLoading;
            cancel.IsVisible = session.IsLoading;
            status.Text = session.IsLoading ? "Reading vocabulary…" : session.Error is { } error
                ? error + (session.Report is null ? "" : " Previous observations may be stale.")
                : session.Report is { } report ? report.Scope + $" Checked {report.CheckedAt.ToLocalTime():HH:mm:ss}." : "Discover labels, classes and properties used in stored data.";
            rows.Children.Clear();
            if (session.Report is not { } data)
            {
                return;
            }

            var filtered = data.Entries.Where(e => e.Name.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var group in filtered.GroupBy(e => e.Category))
            {
                var items = new StackPanel { Spacing = 4 };
                foreach (var entry in group)
                {
                    if (entry.Query is { } query)
                    {
                        var button = new Button
                        {
                            Content = new TextBlock { Text = entry.Name, TextWrapping = TextWrapping.Wrap },
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            HorizontalContentAlignment = HorizontalAlignment.Left
                        };
                        button.Click += (_, _) => browse?.Invoke(query);
                        ToolTip.SetTip(button, "Open a query in a Query panel");
                        items.Children.Add(button);
                    }
                    else
                    {
                        items.Children.Add(new SelectableTextBlock { Text = entry.Name, TextWrapping = TextWrapping.Wrap });
                    }
                }
                rows.Children.Add(new Expander { Header = $"{group.Key} · {group.Count()}", Content = items, IsExpanded = true });
            }
            if (filtered.Length == 0)
            {
                rows.Children.Add(new TextBlock { Text = data.Entries.Count == 0 ? "No vocabulary found in these reads." : "No matching entries." });
            }
        }
        void Changed() => Dispatcher.UIThread.Post(Render);
        AttachedToVisualTree += (_, _) =>
        {
            session.Changed += Changed;
            Render();
        };
        DetachedFromVisualTree += (_, _) => session.Changed -= Changed;
        refresh.Click += async (_, _) => await session.RefreshAsync();
        cancel.Click += (_, _) => session.Cancel();
        search.TextChanged += (_, _) => Render();
        Render();
    }
}
