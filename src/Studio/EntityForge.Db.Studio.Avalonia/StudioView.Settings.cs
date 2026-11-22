using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EntityForge.Db.Studio.Settings;
using EntityForge.UI;
using EntityForge.UI.Avalonia;

namespace EntityForge.Db.Studio.Avalonia;

public sealed partial class StudioView
{
    private IStudioClientSettingsStore _settingsStore = new MemoryStudioClientSettingsStore();
    private StudioClientSettings _clientSettings = new();
    private readonly Dictionary<Control, bool> _settingsBackgroundStates = [];
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
    private bool _settingsLoaded;
    private bool _updatingSettings;

    private void InitializeSettings(StudioHostServices services)
    {
        _settingsStore = services.ClientSettings;
        SettingsTheme.ItemsSource = new[] { EntityForgeTheme.Dark, EntityForgeTheme.Light, EntityForgeTheme.Blue };
        SettingsButton.IsEnabled = false;
        AttachedToVisualTree += LoadClientSettings;
    }

    private async void LoadClientSettings(object? sender, VisualTreeAttachmentEventArgs args)
    {
        if (_settingsLoaded)
        {
            return;
        }

        _settingsLoaded = true;
        try
        {
            _clientSettings = await _settingsStore.LoadAsync();
            if (_clientSettings.Theme is not (EntityForgeTheme.Dark or EntityForgeTheme.Light or EntityForgeTheme.Blue))
            {
                _clientSettings = _clientSettings with
                {
                    Theme = EntityForgeTheme.Dark
                };
            }

            EntityForgeThemeManager.Apply(Application.Current!, _clientSettings.Theme);
        }
        catch (Exception)
        {
            SettingsError.Text = "Saved settings could not be loaded. Choose a theme to save your preference again.";
            SettingsError.IsVisible = true;
        }
        finally
        {
            SettingsButton.IsEnabled = true;
        }
    }

    private void OpenSettings(object? sender, RoutedEventArgs args)
    {
        if (SettingsOverlay.IsVisible || AboutOverlay.IsVisible || ModalOverlay.IsVisible)
        {
            return;
        }

        _updatingSettings = true;
        SettingsTheme.SelectedItem = _clientSettings.Theme;
        _updatingSettings = false;
        foreach (Control child in StudioRoot.Children)
        {
            if (ReferenceEquals(child, SettingsOverlay))
            {
                continue;
            }

            _settingsBackgroundStates.Add(child, child.IsEnabled);
            child.IsEnabled = false;
        }
        SettingsOverlay.IsVisible = true;
        SettingsTheme.Focus();
    }

    private async void SelectSettingsTheme(object? sender, SelectionChangedEventArgs args)
    {
        if (_updatingSettings || SettingsTheme.SelectedItem is not EntityForgeTheme theme)
        {
            return;
        }

        _clientSettings = _clientSettings with
        {
            Theme = theme
        };
        EntityForgeThemeManager.Apply(Application.Current!, theme);
        await _settingsSaveGate.WaitAsync();
        try
        {
            await _settingsStore.SaveAsync(_clientSettings);
            SettingsError.IsVisible = false;
        }
        catch (Exception)
        {
            SettingsError.Text = "The theme was applied, but could not be saved. Change the selection to retry.";
            SettingsError.IsVisible = true;
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    private void SettingsOverlayKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape)
        {
            return;
        }

        CloseSettings();
        args.Handled = true;
    }

    private void CloseSettingsClick(object? sender, RoutedEventArgs args) => CloseSettings();

    private void CloseSettings()
    {
        SettingsOverlay.IsVisible = false;
        foreach (var (control, enabled) in _settingsBackgroundStates)
        {
            control.IsEnabled = enabled;
        }

        _settingsBackgroundStates.Clear();
        SettingsButton.Focus();
    }
}
