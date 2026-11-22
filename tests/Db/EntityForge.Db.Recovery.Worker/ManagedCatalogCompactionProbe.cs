using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Hosting;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Recovery.Worker;

public sealed record ManagedCompactionScenario(string DataDirectory, string ManagedDirectory, long Generation, long Version, int Boundary);

public static class ManagedCatalogCompactionProbe
{
    public static readonly string[] Stages = ["metadata-flushed", "metadata-published", "directory-synced"];

    public static async Task RunAsync(string oracle)
    {
        var scenario = JsonSerializer.Deserialize<ManagedCompactionScenario>(await File.ReadAllTextAsync(oracle))!;
        ArgumentOutOfRangeException.ThrowIfNegative(scenario.Boundary);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(scenario.Boundary, Stages.Length);
        const string token = "compaction-process-probe-administrator";
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(scenario.DataDirectory)
            .UsePropertyGraphProfile().UseGqlGraphs(new("main", new Dictionary<string, string>()))
            .ConfigureStorage(options => options.ManagedGqlDirectory = scenario.ManagedDirectory)
            .UseBearerTokenSecurity(new(token, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var observer = new Boundary(Stages[scenario.Boundary]);
        using var subscription = DiagnosticListener.AllListeners.Subscribe(observer);
        using var http = new HttpClient { BaseAddress = new Uri(host.Application.Urls.Single()), Timeout = TimeSpan.FromMinutes(1) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.PostAsJsonAsync("/v1/administration/gql/catalog/compact", new
        {
            scenario.Generation,
            scenario.Version
        });
        throw new InvalidOperationException("Selected compaction boundary was not terminated: " + await response.Content.ReadAsStringAsync());
    }

    private sealed class Boundary(string selected) : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly List<IDisposable> _subscriptions = [];

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "EntityForge.Db.Hosting.ManagedCatalogPublication")
            {
                _subscriptions.Add(listener.Subscribe(this));
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == selected)
            {
                Console.WriteLine("READY " + selected);
                Console.Out.Flush();
                Console.ReadLine();
                throw new IOException("Compaction controller disconnected without terminating the worker.");
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error) => throw error;

        public void Dispose()
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }
        }
    }
}
