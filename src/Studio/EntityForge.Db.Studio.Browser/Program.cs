using Avalonia;
using Avalonia.Browser;
using EntityForge.Db.Studio.Avalonia;
using System.Runtime.InteropServices.JavaScript;

namespace EntityForge.Db.Studio.Browser;

internal static partial class Program
{
    private static async Task Main(string[] args)
    {
        await JSHost.ImportAsync("studioHost", "../studioHost.js");
        string? demoToken = TakeDemoToken();
        App.HostServices = new StudioHostServices(
            new StudioHostCapabilities(true, true, true, false),
            new BrowserWorkspaceStore(),
            new BrowserConnectionProfileStore(),
            new Uri(GetOrigin()))
        {
            ClientSettings = new BrowserClientSettingsStore(),
            QueryPresets = new(new BrowserQueryPresetStore()),
            ExportQueryPresets = json =>
            {
                DownloadPresets(json);
                return Task.CompletedTask;
            },
            ImportQueryPresets = ImportPresets
        };
        if (demoToken is not null)
        {
            App.HostServices = StudioHostServices.CreateTransient(new Uri(GetOrigin())) with
            {
                TakeDemoToken = () =>
                {
                    string? value = demoToken;
                    demoToken = null;
                    return value;
                }
            };
        }
        await AppBuilder.Configure<App>().WithInterFont().StartBrowserAppAsync("out");
    }

    [JSImport("downloadPresets", "studioHost")]
    private static partial void DownloadPresets(string json);

    [JSImport("importPresets", "studioHost")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string?> ImportPresets();

    [JSImport("getOrigin", "studioHost")]
    private static partial string GetOrigin();

    [JSImport("takeDemoToken", "studioHost")]
    private static partial string? TakeDemoToken();
}
