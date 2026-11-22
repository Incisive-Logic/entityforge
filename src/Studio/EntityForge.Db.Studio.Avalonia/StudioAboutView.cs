using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EntityForge.UI;
using EntityForge.UI.Avalonia.Controls;

namespace EntityForge.Db.Studio.Avalonia;

internal sealed class StudioAboutView : Border
{
    protected override Type StyleKeyOverride => typeof(Border);

    public event Action? CloseRequested;

    public StudioAboutView()
    {
        Padding = new Thickness(20);
        Classes.Add("ef-card");
        var cube = new RetroForgeCube { Height = 310 };
        global::Avalonia.Automation.AutomationProperties.SetName(cube, "Rotating open-frame EntityForge cube with a central spark");
        string version = typeof(StudioAboutView).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(StudioAboutView).Assembly.GetName().Version?.ToString() ?? "Development Build";
        version = version.Split('+')[0];
        var title = new TextBlock { Text = EntityForgeBrandTokens.StudioName, Classes = { "ef-title" } };
        var detail = new TextBlock { Text = $"Version {version}", Classes = { "ef-muted" } };
        var website = new Button { Content = "entityforge.io" };
        website.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                {
                    await launcher.LaunchUriAsync(new Uri("https://entityforge.io"));
                }
            }
            catch (Exception)
            {
                detail.Text = "Visit https://entityforge.io in your browser.";
            }
        };
        var close = new Button { Content = "Close" };
        close.Classes.Add("ef-primary");
        close.Click += (_, _) => CloseRequested?.Invoke();
        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 8,
            Children = { website, close }
        };
        Child = new StackPanel
        {
            Spacing = 12,
            Children =
        {
            new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = cube },
            title, detail,
            new TextBlock { Text = "Explore entities, relationships and the evidence behind them.", TextWrapping = TextWrapping.Wrap, Classes = { "ef-muted" } },
            actions,
        }
        };
    }
}
