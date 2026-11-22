using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace EntityForge.UI.Avalonia.Workspace;

/// <summary>Common, always-interactive panel chrome, independent of the module's content state.</summary>
public sealed class WorkspacePanelHeader : Border
{
    public StackPanel Controls { get; } = new() { Spacing = 6 };

    public StackPanel Actions { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 6 };

    public event Action<string>? RenameRequested;

    private bool _canRename;
    private Action? _updateRename;

    public bool CanRename
    {
        get => _canRename;
        set
        {
            _canRename = value;
            _updateRename?.Invoke();
        }
    }

    public WorkspacePanelHeader(string title, string typeName, bool canRename = true)
    {
        Padding = new Thickness(12, 8);
        Classes.Add("ef-module-header");
        var label = new TextBlock
        {
            Text = title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "ef-panel-title" }
        };
        _updateRename = () => ToolTip.SetTip(label, CanRename ? typeName + " — Double-click to rename" : typeName);
        CanRename = canRename;
        var titleHost = new ContentControl { Content = label };
        label.DoubleTapped += (_, e) =>
        {
            if (!CanRename)
            {
                return;
            }

            var edit = new TextBox { Text = label.Text, MinWidth = 100 };
            titleHost.Content = edit;
            edit.KeyDown += (_, key) =>
            {
                if (key.Key == Key.Enter && !String.IsNullOrWhiteSpace(edit.Text))
                {
                    label.Text = edit.Text.Trim();
                    titleHost.Content = label;
                    RenameRequested?.Invoke(label.Text);
                    key.Handled = true;
                }
                else if (key.Key == Key.Escape)
                {
                    titleHost.Content = label;
                    key.Handled = true;
                }
            };
            edit.Focus();
            edit.SelectAll();
            e.Handled = true;
        };
        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleRow.Children.Add(titleHost);
        Grid.SetColumn(Actions, 1);
        titleRow.Children.Add(Actions);
        Controls.Children.Add(titleRow);
        Child = Controls;
    }
}
