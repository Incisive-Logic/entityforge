using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using EntityForge.UI.Workspace;

namespace EntityForge.UI.Avalonia.Workspace;

public enum WorkspacePresentationMode
{
    Auto,
    Wide,
    Stack,
    Focus,
}

/// <summary>
/// Chooses a presentation for a workspace document. It composes the split renderer on wide
/// surfaces and never rewrites that canonical tree when compact modules are reordered.
/// </summary>
public sealed class AdaptiveWorkspaceControl : ContentControl
{
    public static readonly StyledProperty<WorkspaceLayout?> LayoutProperty =
        AvaloniaProperty.Register<AdaptiveWorkspaceControl, WorkspaceLayout?>(nameof(Layout));

    public static readonly StyledProperty<bool> IsEditingProperty =
        AvaloniaProperty.Register<AdaptiveWorkspaceControl, bool>(nameof(IsEditing));

    public static readonly StyledProperty<WorkspacePresentationMode> PresentationModeProperty =
        AvaloniaProperty.Register<AdaptiveWorkspaceControl, WorkspacePresentationMode>(nameof(PresentationMode));

    public static readonly StyledProperty<double> WideBreakpointProperty =
        AvaloniaProperty.Register<AdaptiveWorkspaceControl, double>(nameof(WideBreakpoint), 900);

    public static readonly StyledProperty<double> FocusBreakpointProperty =
        AvaloniaProperty.Register<AdaptiveWorkspaceControl, double>(nameof(FocusBreakpoint), 560);

    public static readonly StyledProperty<double> SplitterThicknessProperty =
        AvaloniaProperty.Register<AdaptiveWorkspaceControl, double>(nameof(SplitterThickness), 8);

    private WorkspaceResponsivePreferences _responsivePreferences = new();
    private string? _focusedModuleId;
    private WorkspacePresentationMode? _renderedMode;
    private readonly List<Action> _editUpdates = [];
    private bool _acceptingRenderedLayout;

    static AdaptiveWorkspaceControl()
    {
        LayoutProperty.Changed.AddClassHandler<AdaptiveWorkspaceControl>((control, _) =>
        {
            if (!control._acceptingRenderedLayout)
            {
                control.Rebuild();
            }
        });
        IsEditingProperty.Changed.AddClassHandler<AdaptiveWorkspaceControl>((control, _) => control.UpdateEditing());
        PresentationModeProperty.Changed.AddClassHandler<AdaptiveWorkspaceControl>((control, _) => control.Rebuild());
        WideBreakpointProperty.Changed.AddClassHandler<AdaptiveWorkspaceControl>((control, _) => control.Rebuild());
        FocusBreakpointProperty.Changed.AddClassHandler<AdaptiveWorkspaceControl>((control, _) => control.Rebuild());
        SplitterThicknessProperty.Changed.AddClassHandler<AdaptiveWorkspaceControl>((control, _) => control.Rebuild());
    }

    public AdaptiveWorkspaceControl() => SizeChanged += (_, _) =>
    {
        if (_renderedMode != EffectivePresentationMode)
        {
            Rebuild();
        }
    };

    public Func<WorkspaceModuleNode, Control>? ModuleFactory
    {
        get; set;
    }

    public Func<WorkspaceModuleNode, IEnumerable<Control>>? ModuleEditToolsFactory
    {
        get; set;
    }

    public Func<WorkspaceModuleNode, string>? ModuleTitleFactory
    {
        get; set;
    }

    public event EventHandler<WorkspaceModuleSplitRequestedEventArgs>? ModuleSplitRequested;

    public event EventHandler<WorkspaceModuleRequestedEventArgs>? ModuleChangeRequested;

    public event EventHandler<WorkspaceModuleRequestedEventArgs>? ModuleRemoveRequested;

    public event EventHandler? ResponsivePreferencesChanged;

    /// <summary>Allows the host to capture transient panel state before views are replaced.</summary>
    public event EventHandler? Rebuilding;

    public WorkspaceLayout? Layout
    {
        get => GetValue(LayoutProperty); set => SetValue(LayoutProperty, value);
    }

    public bool IsEditing
    {
        get => GetValue(IsEditingProperty); set => SetValue(IsEditingProperty, value);
    }

    public WorkspacePresentationMode PresentationMode
    {
        get => GetValue(PresentationModeProperty); set => SetValue(PresentationModeProperty, value);
    }

    public double WideBreakpoint
    {
        get => GetValue(WideBreakpointProperty); set => SetValue(WideBreakpointProperty, value);
    }

    public double FocusBreakpoint
    {
        get => GetValue(FocusBreakpointProperty); set => SetValue(FocusBreakpointProperty, value);
    }

    public double SplitterThickness
    {
        get => GetValue(SplitterThicknessProperty); set => SetValue(SplitterThicknessProperty, value);
    }

    public WorkspaceResponsivePreferences ResponsivePreferences
    {
        get => _responsivePreferences;
        set
        {
            _responsivePreferences = value ?? new WorkspaceResponsivePreferences();
            _focusedModuleId = _responsivePreferences.PreferredFocusedModuleId;
            Rebuild();
        }
    }

    public WorkspacePresentationMode EffectivePresentationMode
        => PresentationMode != WorkspacePresentationMode.Auto
            ? PresentationMode
            : Bounds.Width <= 0 || Bounds.Width >= WideBreakpoint
                ? WorkspacePresentationMode.Wide
                : Bounds.Width < FocusBreakpoint
                    ? WorkspacePresentationMode.Focus
                    : WorkspacePresentationMode.Stack;

    /// <summary>Recreates product-owned module views without changing the canonical layout.</summary>
    public void RefreshModules() => Rebuild();

    private void Rebuild()
    {
        Rebuilding?.Invoke(this, EventArgs.Empty);
        _editUpdates.Clear();
        _renderedMode = EffectivePresentationMode;
        if (Layout is null)
        {
            Content = null;
            return;
        }
        Content = EffectivePresentationMode switch
        {
            WorkspacePresentationMode.Wide => RenderWide(),
            WorkspacePresentationMode.Focus => RenderFocus(),
            _ => RenderStack(),
        };
    }

    private void UpdateEditing()
    {
        foreach (var update in _editUpdates)
        {
            update();
        }
    }

    private Control RenderWide()
    {
        var control = new WorkspaceLayoutControl
        {
            Layout = Layout,
            IsEditing = IsEditing,
            SplitterThickness = SplitterThickness,
            ModuleFactory = ModuleFactory,
            ModuleEditToolsFactory = ModuleEditToolsFactory,
        };
        control.ModuleSplitRequested += (_, args) => ModuleSplitRequested?.Invoke(this, args);
        control.ModuleChangeRequested += (_, args) => ModuleChangeRequested?.Invoke(this, args);
        control.ModuleRemoveRequested += (_, args) => ModuleRemoveRequested?.Invoke(this, args);
        control.LayoutChanged += (_, _) =>
        {
            if (!Equals(Layout, control.Layout))
            {
                _acceptingRenderedLayout = true;
                try
                {
                    Layout = control.Layout;
                }
                finally
                {
                    _acceptingRenderedLayout = false;
                }
            }
        };
        control.RefreshModules();
        _editUpdates.Add(() => control.IsEditing = IsEditing);
        return control;
    }

    private Control RenderStack()
    {
        var stack = new StackPanel { Spacing = SplitterThickness };
        IReadOnlyList<WorkspaceModuleNode> modules = OrderedModules();
        for (int index = 0; index < modules.Count; index++)
        {
            stack.Children.Add(RenderCompactModule(modules[index], index, modules.Count));
        }
        return new ScrollViewer { Content = stack };
    }

    private Control RenderFocus()
    {
        IReadOnlyList<WorkspaceModuleNode> modules = OrderedModules();
        WorkspaceModuleNode selected = modules.FirstOrDefault(module => module.Id == _focusedModuleId) ?? modules[0];
        _focusedModuleId = selected.Id;

        var navigation = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        foreach (WorkspaceModuleNode module in modules)
        {
            var button = new Button { Content = Title(module), MinHeight = 36 };
            global::Avalonia.Automation.AutomationProperties.SetName(button, "Show " + Title(module));
            button.Classes.Add(module.Id == selected.Id ? "ef-primary" : "ef-command");
            button.Click += (_, _) =>
            {
                _focusedModuleId = module.Id;
                SaveFocusPreference();
                Rebuild();
            };
            navigation.Children.Add(button);
        }
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(navigation);
        Control moduleContent = RenderCompactModule(selected, modules.IndexOf(selected), modules.Count);
        Grid.SetRow(moduleContent, 1);
        grid.Children.Add(moduleContent);
        return grid;
    }

    private Control RenderCompactModule(WorkspaceModuleNode module, int index, int count)
    {
        Control content = ModuleFactory?.Invoke(module) ?? new TextBlock { Text = module.ModuleKey };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(4) };
        foreach (var tool in ModuleEditToolsFactory?.Invoke(module) ?? [])
        {
            toolbar.Children.Add(tool);
        }

        toolbar.Children.Add(OrderButton("↑", "Move Earlier", index > 0, () => Move(module.Id, -1)));
        toolbar.Children.Add(OrderButton("↓", "Move Later", index < count - 1, () => Move(module.Id, 1)));
        var change = OrderButton("▣", "Change Module", true, () => ModuleChangeRequested?.Invoke(this, new WorkspaceModuleRequestedEventArgs(module)));
        toolbar.Children.Add(change);
        var remove = OrderButton("×", "Remove Module", count > 1, () => ModuleRemoveRequested?.Invoke(this, new WorkspaceModuleRequestedEventArgs(module)));
        toolbar.Children.Add(remove);
        grid.Children.Add(toolbar);
        toolbar.IsVisible = IsEditing;
        _editUpdates.Add(() => toolbar.IsVisible = IsEditing);
        Grid.SetRow(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private static Button OrderButton(string glyph, string tooltip, bool enabled, Action action)
    {
        var button = new Button { Content = glyph, Width = 32, Height = 28, Padding = new Thickness(0), IsEnabled = enabled };
        button.Classes.Add("ef-command");
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private IReadOnlyList<WorkspaceModuleNode> OrderedModules()
        => WorkspaceLayoutTraversal.OrderForCompactPresentation(Layout!, ResponsivePreferences);

    private string Title(WorkspaceModuleNode module) => ModuleTitleFactory?.Invoke(module) ?? module.ModuleKey;

    private void Move(string moduleId, int delta)
    {
        var ids = OrderedModules().Select(module => module.Id).ToList();
        int source = ids.IndexOf(moduleId);
        int target = source + delta;
        if (source < 0 || target < 0 || target >= ids.Count)
        {
            return;
        }

        (ids[source], ids[target]) = (ids[target], ids[source]);
        _responsivePreferences = _responsivePreferences with
        {
            CompactModuleOrder = ids
        };
        ResponsivePreferencesChanged?.Invoke(this, EventArgs.Empty);
        Rebuild();
    }

    private void SaveFocusPreference()
    {
        _responsivePreferences = _responsivePreferences with
        {
            PreferredFocusedModuleId = _focusedModuleId
        };
        ResponsivePreferencesChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal static class WorkspaceModuleListExtensions
{
    public static int IndexOf(this IReadOnlyList<WorkspaceModuleNode> modules, WorkspaceModuleNode selected)
    {
        for (int index = 0; index < modules.Count; index++)
        {
            if (modules[index].Id == selected.Id)
            {
                return index;
            }
        }

        return 0;
    }
}
