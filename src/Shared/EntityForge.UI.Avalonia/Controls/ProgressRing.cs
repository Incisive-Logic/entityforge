using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace EntityForge.UI.Avalonia.Controls;

/// <summary>A compact indeterminate activity indicator styled by the shared theme.</summary>
public sealed class ProgressRing : TemplatedControl
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<ProgressRing, bool>(nameof(IsActive));

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty)
        {
            PseudoClasses.Set(":active", IsActive);
        }
    }
}
