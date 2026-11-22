using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;

namespace EntityForge.UI.Avalonia.Controls;

/// <summary>Single-selection panel navigation with shared theme styling and keyboard navigation.</summary>
public sealed class PanelTabs : ListBox
{
    protected override Type StyleKeyOverride => typeof(ListBox);

    public PanelTabs()
    {
        Classes.Add("ef-panel-tabs");
        ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal });
    }
}
