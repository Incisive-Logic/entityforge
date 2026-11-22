using Avalonia.Controls;

namespace EntityForge.Db.Studio.Avalonia;

/// <summary>Keeps navigation outside the inspection viewport.</summary>
internal class InspectionPanel : Grid
{
    public StackPanel Header { get; } = new() { Spacing = 6, Margin = new(0, 0, 0, 8) };

    public StackPanel Body { get; } = new() { Spacing = 8 };

    public ScrollViewer Viewport
    {
        get;
    }

    public InspectionPanel()
    {
        RowDefinitions = new("Auto,*");
        Viewport = new ScrollViewer { Content = Body };
        Children.Add(Header);
        SetRow(Viewport, 1);
        Children.Add(Viewport);
    }

    internal static void Reconcile(StackPanel panel, IReadOnlyList<Control> desired)
    {
        for (int i = panel.Children.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(panel.Children[i]))
            {
                panel.Children.RemoveAt(i);
            }
        }

        for (int i = 0; i < desired.Count; i++)
        {
            if (i < panel.Children.Count && ReferenceEquals(panel.Children[i], desired[i]))
            {
                continue;
            }

            panel.Children.Remove(desired[i]);
            panel.Children.Insert(i, desired[i]);
        }
    }
}
