using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input.Platform;
using AvaloniaEdit;
using EntityForge.UI.Avalonia.Controls;

namespace EntityForge.Db.Studio.Avalonia;

/// <summary>Switches presentation without executing a query or changing its result.</summary>
internal sealed class ResultPresentationControl : Grid
{
    public ScrollViewer ResultsScroller
    {
        get;
    }

    public ResultPresentationControl(Control view, Func<string> format, bool showSyntax,
        Action<bool> remember, Control? pageSummary = null)
    {
        RowDefinitions = new RowDefinitions("Auto,Auto,*");
        RowSpacing = 8;
        var mode = new PanelTabs { ItemsSource = new[] { "Results", "JSON" }, SelectedIndex = showSyntax ? 1 : 0 };
        global::Avalonia.Automation.AutomationProperties.SetName(mode, "Result presentation");
        var copyIcon = new global::Avalonia.Controls.Shapes.Path();
        copyIcon.Classes.Add("ef-copy-icon");
        var copy = new Button { Content = copyIcon };
        copy.Classes.Add("ef-icon-button");
        ToolTip.SetTip(copy, "Copy JSON");
        global::Avalonia.Automation.AutomationProperties.SetName(copy, "Copy JSON");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        status.Classes.Add("ef-muted");
        var host = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        ResultsScroller = new ScrollViewer
        {
            Content = view,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        TextEditor? syntax = null;
        string? json = null;
        string Json() => json ??= format();
        void Show()
        {
            bool selected = mode.SelectedIndex == 1;
            if (selected && syntax is null)
            {
                syntax = new TextEditor
                {
                    Text = Json(),
                    IsReadOnly = true,
                    ShowLineNumbers = true,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
                    FontSize = 13,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                global::Avalonia.Automation.AutomationProperties.SetName(syntax, "Result JSON");
                syntax.TextArea.TextView.LineTransformers.Add(new JsonSyntaxColourizer());
                syntax.ActualThemeVariantChanged += (_, _) => syntax.TextArea.TextView.Redraw();
            }
            host.Content = selected ? syntax : ResultsScroller;
            remember(selected);
        }
        mode.SelectionChanged += (_, _) => Show();
        copy.Click += async (_, _) =>
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is null)
                {
                    throw new InvalidOperationException("Clipboard unavailable.");
                }

                await clipboard.SetTextAsync(Json());
                status.Text = "JSON copied.";
            }
            catch (Exception)
            {
                mode.SelectedIndex = 1;
                status.Text = "Could not copy to the clipboard. Select the JSON text and copy it manually.";
            }
            status.IsVisible = true;
        };
        var toolbar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 12,
            LineSpacing = 6,
            Children = { mode, copy }
        };
        if (pageSummary is not null)
        {
            toolbar.Children.Add(pageSummary);
        }

        Children.Add(toolbar);
        Grid.SetRow(status, 1);
        Grid.SetRow(host, 2);
        Children.Add(status);
        Children.Add(host);
        Show();
    }
}
