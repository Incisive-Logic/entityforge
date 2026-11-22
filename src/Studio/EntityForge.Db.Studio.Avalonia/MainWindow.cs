using Avalonia.Controls;

namespace EntityForge.Db.Studio.Avalonia;

/// <summary>Desktop-only window host for the shared Studio view.</summary>
public sealed class MainWindow : Window
{
    /// <summary>Creates the desktop window around a host-neutral Studio view.</summary>
    public MainWindow(StudioView view)
    {
        Title = "EntityForge DB Studio";
        Icon = new WindowIcon(global::Avalonia.Platform.AssetLoader.Open(
            new Uri("avares://EntityForge.UI.Avalonia/Assets/Brand/entity-forge.ico")));
        Width = 1440;
        Height = 900;
        MinWidth = 880;
        MinHeight = 600;
        Content = view;
    }
}
