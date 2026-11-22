using System.Globalization;
using System.Security.Cryptography;
using EntityForge.Db.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace EntityForge.Db.Server;

internal static class SemanticDemo
{
    internal static async Task RunAsync(string[] args)
    {
        int port = 8080;
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--port" ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 0 or > 65535))
        {
            Console.Error.WriteLine("Usage: EntityForge.Db.Server demo [--port 8080]");
            Environment.ExitCode = 1;
            return;
        }
        string assets = Environment.GetEnvironmentVariable("ENTITYFORGE_DB_STUDIO_ASSETS") ?? Path.Combine(AppContext.BaseDirectory, "studio");
        if (!File.Exists(Path.Combine(assets, "demo.html")))
        {
            Console.Error.WriteLine("Demo needs the bundled Studio. Use the portable server-and-Studio package, or set ENTITYFORGE_DB_STUDIO_ASSETS to the published Studio wwwroot.");
            Environment.ExitCode = 1;
            return;
        }
        string admin = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string reader = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var directory = Directory.CreateTempSubdirectory("entityforge-demo-");
        try
        {
            await using var host = EntityForgeDbServer.CreateBuilder()
                .UseSemanticProfile().UseLocalStorage(directory.FullName).UseStudio(assets)
                .UseBearerTokenSecurity(new(admin, readerToken: reader, requireHttps: false))
                .ConfigureWebHost(web => web.UseUrls($"http://127.0.0.1:{port}")).Build();
            await host.StartAsync();
            var address = new Uri(host.Application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Authorization = new("Bearer", admin);
            await SemanticDemoData.LoadAsync(http);
            Console.WriteLine();
            Console.WriteLine("Your Semantic sample is ready. Open this link:");
            Console.WriteLine(new Uri(address, "/studio/demo.html").AbsoluteUri + "#demo=" + reader);
            Console.WriteLine("Local, read-only tour. Ctrl+C stops it and removes this disposable sample.");
            Console.WriteLine("A fresh sample is prepared on every start; existing databases are never opened.");
            await host.Application.WaitForShutdownAsync();
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine("Demo could not start: " + failure.Message);
            Environment.ExitCode = 1;
        }
        finally
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                Console.Error.WriteLine("Temporary sample remains at " + directory.FullName);
            }
            catch (UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Temporary sample remains at " + directory.FullName);
            }
        }
    }
}
