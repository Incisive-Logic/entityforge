using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace EntityForge.Db.Studio.Avalonia;

/// <summary>Keeps execution accessible while a query document scrolls.</summary>
internal sealed class QueryPanelControl : Grid
{
    public QueryPanelControl(ScrollViewer editor, Control? actions)
    {
        RowDefinitions = new RowDefinitions("*,Auto");
        Children.Add(editor);
        if (actions is not null)
        {
            var footer = new Border { Padding = new Thickness(14, 6, 14, 10), Child = actions };
            SetRow(footer, 1);
            Children.Add(footer);
            ToolTip.SetTip(footer, "Ctrl+Enter to run the query");
            AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.Control)
                {
                    return;
                }

                var run = (actions is Button button ? new[] { button } : actions.GetLogicalDescendants().OfType<Button>())
                    .FirstOrDefault(button => button.Classes.Contains("ef-primary") && button.IsEffectivelyEnabled && button.IsVisible);
                if (run is null)
                {
                    return;
                }

                run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
        }
    }
}
