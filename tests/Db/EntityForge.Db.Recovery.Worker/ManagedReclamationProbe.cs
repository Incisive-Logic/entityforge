using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Hosting;
using EntityForge.Db.Storage.Pages;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Recovery.Worker;

public sealed record ManagedReclamationScenario(string DataDirectory, string ManagedDirectory, Guid StoreId, Guid Incarnation, int Boundary);

public static class ManagedReclamationProbe
{
    public static readonly string[] Stages = ["marker-flushed", "marker-published", "intent-synced", "payload-deleted", "journal-removed", "complete"];

    public static async Task RunAsync(string mode, string oracle)
    {
        var scenario = JsonSerializer.Deserialize<ManagedReclamationScenario>(await File.ReadAllTextAsync(oracle))!;
        string store = Path.Combine(scenario.ManagedDirectory, scenario.StoreId.ToString("N"));
        if (mode == "--reclamation-owner-worker")
        {
            await using var database = await GraphDatabase.OpenLocalAsync(new(store));
            WaitForController("owner");
            throw new IOException("Owner controller disconnected without terminating the worker.");
        }
        if (mode == "--reclamation-delayed-open-worker")
        {
            using var listener = new MeterListener();
            bool reached = false;
            listener.InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == "EntityForge.Db.Graph" && instrument.Name == "entityforge.db.graph.open.phase.duration")
                {
                    owner.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
            {
                foreach (var tag in tags)
                {
                    if (!reached && tag.Key == "entityforge.db.phase" && Equals(tag.Value, "format"))
                    {
                        reached = true;
                        WaitForController("format");
                    }
                }
            });
            listener.Start();
            try
            {
                await using var database = await GraphDatabase.OpenLocalAsync(new(store));
            }
            catch (PhysicalPageIoException exception) when (reached && exception.InnerException is InvalidDataException)
            {
                Console.WriteLine("REFUSED retired location after delayed format discovery");
                return;
            }
            throw new InvalidOperationException("The delayed opener did not refuse the retired location.");
        }
        if (scenario.Boundary < 0 || scenario.Boundary >= Stages.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(scenario.Boundary));
        }
        using var observer = new Boundary(Stages[scenario.Boundary]);
        using var subscription = DiagnosticListener.AllListeners.Subscribe(observer);
        const string token = "reclamation-process-probe-administrator";
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(scenario.DataDirectory)
            .UsePropertyGraphProfile().UseGqlGraphs(new("main", new Dictionary<string, string>()))
            .ConfigureStorage(options => options.ManagedGqlDirectory = scenario.ManagedDirectory)
            .UseBearerTokenSecurity(new(token, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(host.Application.Urls.Single()), Timeout = TimeSpan.FromMinutes(1) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.PostAsJsonAsync($"/v1/administration/gql/retention/{scenario.StoreId}/reclaim",
            new
            {
                scenario.Incarnation
            });
        throw new InvalidOperationException("Selected reclamation boundary was not terminated: " + await response.Content.ReadAsStringAsync());
    }

    private static void WaitForController(string boundary)
    {
        Console.WriteLine("READY " + boundary);
        Console.Out.Flush();
        // EOF releases a worker if its controlling test process disappears.
        Console.ReadLine();
    }

    private sealed class Boundary(string selected) : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly List<IDisposable> _subscriptions = [];

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "EntityForge.Db.Hosting.ManagedReclamation")
            {
                _subscriptions.Add(listener.Subscribe(this));
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == selected)
            {
                WaitForController(selected);
                throw new IOException("Reclamation controller disconnected without terminating the worker.");
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
