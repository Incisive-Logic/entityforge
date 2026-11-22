using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace EntityForge.Db.Studio.Avalonia;

public sealed partial class StudioView
{
    private readonly Dictionary<Control, bool> _aboutBackgroundStates = [];
    private Control? _aboutReturnFocus;

    private void OpenAbout(object? sender, RoutedEventArgs args)
    {
        if (AboutOverlay.IsVisible || ModalOverlay.IsVisible || SettingsOverlay.IsVisible)
        {
            return;
        }

        var about = new StudioAboutView { Focusable = true };
        about.AttachedToVisualTree += (_, _) => about.Focus();
        KeyboardNavigation.SetTabNavigation(about, KeyboardNavigationMode.Cycle);
        about.CloseRequested += CloseAbout;
        _aboutReturnFocus = sender as Control;
        foreach (Control child in StudioRoot.Children)
        {
            if (ReferenceEquals(child, AboutOverlay))
            {
                continue;
            }

            _aboutBackgroundStates.Add(child, child.IsEnabled);
            child.IsEnabled = false;
        }
        AboutContent.Content = about;
        AboutOverlay.IsVisible = true;
        about.Focus();
    }

    private void AboutOverlayKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape)
        {
            return;
        }

        CloseAbout();
        args.Handled = true;
    }

    private void CloseAbout()
    {
        AboutOverlay.IsVisible = false;
        // Removing the content detaches the animation and stops its timer.
        AboutContent.Content = null;
        foreach (var (control, enabled) in _aboutBackgroundStates)
        {
            control.IsEnabled = enabled;
        }

        _aboutBackgroundStates.Clear();
        _aboutReturnFocus?.Focus();
        _aboutReturnFocus = null;
    }
}
