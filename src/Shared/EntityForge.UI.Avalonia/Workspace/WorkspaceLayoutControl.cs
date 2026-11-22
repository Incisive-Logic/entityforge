using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using EntityForge.UI.Workspace;

namespace EntityForge.UI.Avalonia.Workspace;

public sealed class WorkspaceModuleSplitRequestedEventArgs(
    WorkspaceModuleNode module,
    WorkspaceSplitOrientation orientation) : EventArgs
{
    public WorkspaceModuleNode Module { get; } = module;

    public WorkspaceSplitOrientation Orientation { get; } = orientation;
}

public sealed class WorkspaceModuleRequestedEventArgs(WorkspaceModuleNode module) : EventArgs
{
    public WorkspaceModuleNode Module { get; } = module;
}

/// <summary>
/// Avalonia renderer for a neutral workspace document. It owns no product modules,
/// persistence, commands, or layout mutation policy.
/// </summary>
public sealed class WorkspaceLayoutControl : ContentControl
{
    public static readonly StyledProperty<WorkspaceLayout?> LayoutProperty =
        AvaloniaProperty.Register<WorkspaceLayoutControl, WorkspaceLayout?>(nameof(Layout));

    public static readonly StyledProperty<bool> IsEditingProperty =
        AvaloniaProperty.Register<WorkspaceLayoutControl, bool>(nameof(IsEditing));

    public static readonly StyledProperty<double> SplitterThicknessProperty =
        AvaloniaProperty.Register<WorkspaceLayoutControl, double>(nameof(SplitterThickness), 8);

    static WorkspaceLayoutControl()
    {
        LayoutProperty.Changed.AddClassHandler<WorkspaceLayoutControl>((control, _) => control.OnLayoutChanged());
        IsEditingProperty.Changed.AddClassHandler<WorkspaceLayoutControl>((control, _) => control.UpdateEditing());
        SplitterThicknessProperty.Changed.AddClassHandler<WorkspaceLayoutControl>((control, _) => control.Rebuild());
    }

    /// <summary>Creates a fresh product-owned view for a module leaf.</summary>
    public Func<WorkspaceModuleNode, Control>? ModuleFactory
    {
        get; set;
    }

    public Func<WorkspaceModuleNode, IEnumerable<Control>>? ModuleEditToolsFactory
    {
        get; set;
    }

    public event EventHandler<WorkspaceModuleSplitRequestedEventArgs>? ModuleSplitRequested;

    public event EventHandler<WorkspaceModuleRequestedEventArgs>? ModuleChangeRequested;

    public event EventHandler<WorkspaceModuleRequestedEventArgs>? ModuleRemoveRequested;

    public event EventHandler? LayoutChanged;

    public WorkspaceLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public bool IsEditing
    {
        get => GetValue(IsEditingProperty);
        set => SetValue(IsEditingProperty, value);
    }

    public double SplitterThickness
    {
        get => GetValue(SplitterThicknessProperty);
        set => SetValue(SplitterThicknessProperty, value);
    }

    public void RefreshModules() => Rebuild();

    private readonly List<Action> _editUpdates = [];
    private bool _persistingProportion;
    private int _renderRevision;

    private void UpdateEditing()
    {
        foreach (var update in _editUpdates)
        {
            update();
        }
    }

    private void OnLayoutChanged()
    {
        if (!_persistingProportion)
        {
            Rebuild();
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Rebuild()
    {
        _renderRevision++;
        _editUpdates.Clear();
        Content = Layout is null ? null : Render(Layout.Root);
    }

    private Control Render(WorkspaceLayoutNode node)
        => node switch
        {
            WorkspaceModuleNode module => RenderModule(module),
            WorkspaceSplitNode split => RenderSplit(split),
            _ => throw new InvalidOperationException($"Unsupported workspace node type '{node.GetType().Name}'."),
        };

    private Control RenderModule(WorkspaceModuleNode module)
    {
        Control content = ModuleFactory?.Invoke(module)
            ?? new TextBlock { Text = module.ModuleKey, Margin = new Thickness(12) };

        var editSurface = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(content, 1);
        editSurface.Children.Add(content);

        var tools = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(8),
            Spacing = 4,
        };
        foreach (var tool in ModuleEditToolsFactory?.Invoke(module) ?? [])
        {
            tools.Children.Add(tool);
        }

        tools.Children.Add(EditButton("↔", "Split Vertically", () =>
            ModuleSplitRequested?.Invoke(this, new WorkspaceModuleSplitRequestedEventArgs(module, WorkspaceSplitOrientation.Vertical))));
        tools.Children.Add(EditButton("↕", "Split Horizontally", () =>
            ModuleSplitRequested?.Invoke(this, new WorkspaceModuleSplitRequestedEventArgs(module, WorkspaceSplitOrientation.Horizontal))));
        tools.Children.Add(EditButton("▣", "Change Module", () =>
            ModuleChangeRequested?.Invoke(this, new WorkspaceModuleRequestedEventArgs(module))));
        Button remove = EditButton("×", "Remove Module", () =>
            ModuleRemoveRequested?.Invoke(this, new WorkspaceModuleRequestedEventArgs(module)));
        remove.IsEnabled = Layout is not null && CountModules(Layout.Root) > 1;
        tools.Children.Add(remove);
        editSurface.Children.Add(tools);

        var frame = new Border
        {
            Child = editSurface,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Tag = module.Id,
        };
        void Update()
        {
            tools.IsVisible = IsEditing;
            frame.BorderThickness = new Thickness(IsEditing ? 2 : 0);
            frame.CornerRadius = new CornerRadius(IsEditing ? 10 : 0);
            frame.Classes.Set("ef-workspace-edit-frame", IsEditing);
        }
        _editUpdates.Add(Update);
        Update();
        return frame;
    }

    private static Button EditButton(string glyph, string tooltip, Action action)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 30,
            Height = 28,
            Padding = new Thickness(0),
            HorizontalContentAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
        };
        button.Classes.Add("ef-command");
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private static int CountModules(WorkspaceLayoutNode node)
        => node is WorkspaceSplitNode split
            ? CountModules(split.First) + CountModules(split.Second)
            : 1;

    private Control RenderSplit(WorkspaceSplitNode split)
    {
        var grid = new Grid();
        Control first = Render(split.First);
        Control second = Render(split.Second);
        var splitter = new GridSplitter
        {
            IsHitTestVisible = IsEditing,
            Focusable = IsEditing,
            // Live resizing avoids preview-adorner teardown when another UI refresh
            // detaches a splitter during pointer capture.
            ShowsPreview = false,
            KeyboardIncrement = 8,
        };
        splitter.Classes.Add("ef-workspace-splitter");
        splitter.Classes.Set("editing", IsEditing);
        _editUpdates.Add(() =>
        {
            splitter.IsHitTestVisible = IsEditing;
            splitter.Focusable = IsEditing;
            splitter.Classes.Set("editing", IsEditing);
        });

        int renderRevision = _renderRevision;
        if (split.Orientation == WorkspaceSplitOrientation.Vertical)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(split.FirstProportion, GridUnitType.Star)) { MinWidth = 80 });
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(SplitterThickness)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1 - split.FirstProportion, GridUnitType.Star)) { MinWidth = 80 });
            Grid.SetColumn(first, 0);
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(second, 2);
            splitter.ResizeDirection = GridResizeDirection.Columns;
            splitter.DragCompleted += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    if (renderRevision == _renderRevision)
                    {
                        PersistColumnProportion(split.Id, grid);
                    }
                },
                DispatcherPriority.Background);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(split.FirstProportion, GridUnitType.Star)) { MinHeight = 64 });
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(SplitterThickness)));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1 - split.FirstProportion, GridUnitType.Star)) { MinHeight = 64 });
            Grid.SetRow(first, 0);
            Grid.SetRow(splitter, 1);
            Grid.SetRow(second, 2);
            splitter.ResizeDirection = GridResizeDirection.Rows;
            splitter.DragCompleted += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    if (renderRevision == _renderRevision)
                    {
                        PersistRowProportion(split.Id, grid);
                    }
                },
                DispatcherPriority.Background);
        }

        grid.Children.Add(first);
        grid.Children.Add(splitter);
        grid.Children.Add(second);
        return grid;
    }

    private void PersistColumnProportion(string splitId, Grid grid)
    {
        double total = grid.ColumnDefinitions[0].ActualWidth + grid.ColumnDefinitions[2].ActualWidth;
        PersistProportion(splitId, total <= 0 ? 0.5 : grid.ColumnDefinitions[0].ActualWidth / total);
    }

    private void PersistRowProportion(string splitId, Grid grid)
    {
        double total = grid.RowDefinitions[0].ActualHeight + grid.RowDefinitions[2].ActualHeight;
        PersistProportion(splitId, total <= 0 ? 0.5 : grid.RowDefinitions[0].ActualHeight / total);
    }

    private void PersistProportion(string splitId, double proportion)
    {
        if (Layout is not null)
        {
            // The grid already has the user's new sizes. Persist the model without
            // replacing the live splitter (which can still own pointer capture).
            _persistingProportion = true;
            try
            {
                Layout = WorkspaceLayoutOperations.Resize(Layout, splitId, proportion);
            }
            finally
            {
                _persistingProportion = false;
            }
        }
    }

}
