using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EntityForge.UI;
using EntityForge.UI.Avalonia;

namespace EntityForge.Db.Studio.Avalonia;

public sealed partial class App : Application
{
    public static StudioHostServices HostServices { get; set; } = StudioHostServices.CreateTransient();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        EntityForgeThemeManager.Apply(this, EntityForgeTheme.Dark);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(new StudioView(HostServices));
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new StudioView(HostServices);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
