using System.Diagnostics;
using System.Text.Json;
using EntityForge.Db.Hosting;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Recovery.Worker;

internal sealed record BenchmarkServerConfiguration(string Directory, DatabaseProfileId Profile, string Token);

internal sealed record BenchmarkServerReady(string Address, Guid Incarnation, int ProcessId);

internal sealed record BenchmarkServerObservation(double CpuSeconds, long AllocatedBytes, long WorkingSetBytes, long PeakWorkingSetBytes);

internal static class BenchmarkServer
{
    internal static async Task RunAsync()
    {
        var config = JsonSerializer.Deserialize<BenchmarkServerConfiguration>(await Console.In.ReadLineAsync() ?? throw new IOException("Missing worker configuration."))!;
        var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(config.Directory)
            .UseBearerTokenSecurity(new(config.Token, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0").ConfigureLogging(log => log.ClearProviders()));
        if (config.Profile == DatabaseProfileId.PropertyGraph)
        {
            builder.UsePropertyGraphProfile();
        }
        else if (config.Profile == DatabaseProfileId.Rdf)
        {
            builder.UseRdfProfile();
        }
        await using var host = builder.Build();
        await host.StartAsync();
        string address = host.Application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new(address) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", config.Token);
        using var capabilities = JsonDocument.Parse(await http.GetStringAsync("/v1/capabilities"));
        Guid incarnation = capabilities.RootElement.GetProperty("databaseIncarnation").GetGuid();
        await Reply(new BenchmarkServerReady(address, incarnation, Environment.ProcessId));
        while (await Console.In.ReadLineAsync() is { } command)
        {
            if (command == "stop")
            {
                break;
            }
            if (command == "checkpoint")
            {
                using var response = await http.PostAsync("/v1/checkpoints", null);
                response.EnsureSuccessStatusCode();
            }
            else if (command != "observe")
            {
                throw new ArgumentException("Unknown benchmark worker command.");
            }
            using var process = Process.GetCurrentProcess();
            await Reply(new BenchmarkServerObservation(process.TotalProcessorTime.TotalSeconds, GC.GetTotalAllocatedBytes(), process.WorkingSet64, process.PeakWorkingSet64));
        }
        await host.StopAsync();
    }

    private static async Task Reply<T>(T value)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value));
        await Console.Out.FlushAsync();
    }
}
