using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EntityForge.UI.Workspace;

namespace EntityForge.UI.Avalonia.Workspace;

/// <summary>Reusable source picking UI. Capabilities and connection storage are supplied by the host.</summary>
public sealed class ConnectedPanelPresenter
{
    public PanelConnections Connections { get; set; } = new([]);

    public PanelId? Selecting
    {
        get; private set;
    }

    private bool _isEditing;
    private readonly List<WeakReference<WorkspacePanelHeader>> _headers = [];
    private readonly List<WeakReference<WorkspacePanelEditSurface>> _editSurfaces = [];

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value)
            {
                return;
            }

            _isEditing = value;
            bool cancelSelection = !value && Selecting is not null;
            if (!value)
            {
                Selecting = null;
            }

            _headers.RemoveAll(reference => !reference.TryGetTarget(out _));
            foreach (var reference in _headers)
            {
                if (reference.TryGetTarget(out var header))
                {
                    header.CanRename = value;
                }
            }

            _editSurfaces.RemoveAll(reference => !reference.TryGetTarget(out _));
            foreach (var reference in _editSurfaces)
            {
                if (reference.TryGetTarget(out var surface))
                {
                    surface.SetEditing(value);
                }
            }

            if (cancelSelection)
            {
                Changed?.Invoke();
            }
        }
    }

    public event Action? EditLayoutRequested;

    public void BeginSelection(PanelId id)
    {
        EditLayoutRequested?.Invoke();
        IsEditing = true;
        Selecting = id;
        Changed?.Invoke();
    }

    public IEnumerable<Control> CreateEditTools(PanelId id)
    {
        var panel = Connections.Panels.FirstOrDefault(p => p.Id == id);
        if (panel?.Contract.Accepts is null)
        {
            return [];
        }

        var source = Connections.Panels.FirstOrDefault(p => p.Id == Connections.Source(id));
        var icon = new global::Avalonia.Controls.Shapes.Path
        {
            StrokeThickness = 2,
            Data = Geometry.Parse("M14,9 L16,7 C18,5 21,8 19,10 L15,14 C13,16 10,13 12,11 M10,15 L8,17 C6,19 3,16 5,14 L9,10 C11,8 14,11 12,13"),
        };
        var button = new Button
        {
            Content = new Viewbox
            {
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Canvas { Width = 24, Height = 24, Children = { icon } },
            },
            Width = 30,
            Height = 28,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        icon.Bind(global::Avalonia.Controls.Shapes.Shape.StrokeProperty,
            button.GetObservable(Button.ForegroundProperty));
        global::Avalonia.Automation.AutomationProperties.SetName(button, "Select Source");
        button.Classes.Add("ef-command");
        ToolTip.SetTip(button, source is null ? $"Connect to a {panel.Contract.SourceTypeLabel} panel" : $"Source: {source.Name}");
        button.Click += (_, _) => BeginSelection(id);
        return [button];
    }

    public event Action? Changed;

    public event Action<PanelId, PanelId?>? SourceChanged;

    public event Action<PanelId, string>? NameChanged;

    public void Cancel()
    {
        Selecting = null;
        Changed?.Invoke();
    }

    private void Choose(PanelId target, PanelId? source)
    {
        Selecting = null;
        SourceChanged?.Invoke(target, source);
        Changed?.Invoke();
    }

    public Control Present(ConnectedPanel panel, string typeName, Control content, WorkspacePanelStatus? status = null)
    {
        var header = new WorkspacePanelHeader(panel.Name, typeName, IsEditing);
        _headers.Add(new(header));
        header.RenameRequested += name => NameChanged?.Invoke(panel.Id, name);
        var heading = header.Controls;
        if (IsEditing && Selecting == panel.Id)
        {
            var currentSource = Connections.Panels.FirstOrDefault(p => p.Id == Connections.Source(panel.Id));
            heading.Children.Add(new TextBlock { Text = currentSource is null ? "No Source Selected" : $"Source: {currentSource.Name}", TextWrapping = TextWrapping.Wrap });
            var options = Connections.Panels.Where(p => Connections.CanConnect(panel.Id, p.Id)).ToArray();
            heading.Children.Add(new TextBlock { Text = $"Choose a {panel.Contract.SourceTypeLabel ?? "source"} panel:", TextWrapping = TextWrapping.Wrap });
            var list = new ComboBox
            {
                ItemsSource = options.Select(p => p.Location is null ? p.Name : $"{p.Name} — {p.Location}").ToArray(),
                PlaceholderText = "Compatible Sources",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedIndex >= 0)
                {
                    Choose(panel.Id, options[list.SelectedIndex].Id);
                }
            };
            heading.Children.Add(list);
            if (options.Length == 0)
            {
                heading.Children.Add(new TextBlock { Text = "No compatible panels in this layout." });
            }

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, _) => Cancel();
            var disconnect = new Button { Content = "Disconnect", IsEnabled = Connections.Source(panel.Id) is not null };
            disconnect.Click += (_, _) => Choose(panel.Id, null);
            actions.Children.Add(cancel);
            actions.Children.Add(disconnect);
            heading.Children.Add(actions);
        }
        content = new WorkspacePanelContent(content, status,
            panel.Contract.Accepts is not null && Connections.Source(panel.Id) is null
                ? () => BeginSelection(panel.Id) : null);
        var editSurface = new WorkspacePanelEditSurface(content, IsEditing);
        _editSurfaces.Add(new(editSurface));
        content = editSurface;
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { header, content } };
        Grid.SetRow(content, 1);
        var frame = new Border { Child = grid, Tag = panel.Id.ModuleId, Focusable = true };
        frame.Classes.Add("ef-module");
        if (IsEditing && Selecting is { } target && target != panel.Id)
        {
            bool compatible = Connections.CanConnect(target, panel.Id);
            content.IsHitTestVisible = false;
            frame.Opacity = compatible ? 1 : 0.35;
            if (compatible)
            {
                frame.BorderBrush = Brushes.DodgerBlue;
                frame.BorderThickness = new Thickness(2);
                var choose = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Background = new SolidColorBrush(Color.FromArgb(35, 30, 144, 255))
                };
                choose.Click += (_, _) => Choose(target, panel.Id);
                global::Avalonia.Automation.AutomationProperties.SetName(choose, "Connect to " + panel.Name);
                ToolTip.SetTip(choose, "Use " + panel.Name + " as the source");
                Grid.SetRow(choose, 1);
                grid.Children.Add(choose);
            }
        }
        return frame;
    }
}
