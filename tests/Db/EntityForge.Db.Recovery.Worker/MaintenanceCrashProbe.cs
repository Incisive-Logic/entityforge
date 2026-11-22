using System.Diagnostics;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;

namespace EntityForge.Db.Recovery.Worker;

public static class MaintenanceCrashProbe
{
    public static async Task RunAsync(string oracle)
    {
        var scenario = JsonSerializer.Deserialize<RecoveryScenario>(File.ReadAllText(oracle))!;
        if (scenario.Boundary is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(scenario.Boundary));
        }
        string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(oracle))!, "database");
        using var observer = new Boundary(scenario.Boundary);
        using var subscription = DiagnosticListener.AllListeners.Subscribe(observer);
        if (scenario.Rdf)
        {
            await using var database = await RdfDatabase.OpenLocalAsync(new(directory));
            observer.Armed = true;
            await ((IDatabaseOperationRetention)database).RetainOperationsAsync(DateTimeOffset.UnixEpoch.AddMilliseconds(1));
        }
        else
        {
            await using var database = await GraphDatabase.OpenLocalAsync(new(directory));
            observer.Armed = true;
            await ((IDatabaseOperationRetention)database).RetainOperationsAsync(DateTimeOffset.UnixEpoch.AddMilliseconds(1));
        }
        throw new InvalidOperationException("Selected maintenance crash boundary was not reached.");
    }

    private sealed class Boundary(int boundary) : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly List<IDisposable> _subscriptions = [];
        private static readonly string[] Stages = ["redo", "publish", "flush", "trim"];
        internal bool Armed;

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "EntityForge.Db.Kernel.CheckpointMaintenance")
            {
                _subscriptions.Add(listener.Subscribe(this));
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (!Armed || value.Key != Stages[boundary])
            {
                return;
            }
            Console.WriteLine("READY " + boundary);
            Console.Out.Flush();
            Console.ReadLine();
            throw new IOException("Maintenance controller disconnected without terminating worker.");
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
