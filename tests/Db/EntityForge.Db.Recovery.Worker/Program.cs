using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Recovery.Worker;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Query.Planning;

if (args.Length == 1 && args[0] is "--benchmark-server" or "--benchmark-partition")
{
    try
    {
        if (args[0] == "--benchmark-partition")
        {
            await BenchmarkPartition.RunAsync();
        }
        else
        {
            await BenchmarkServer.RunAsync();
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length == 2 && args[0] is "--reclamation-crash-worker" or "--reclamation-owner-worker" or "--reclamation-delayed-open-worker" or "--compaction-crash-worker")
{
    try
    {
        if (args[0] == "--compaction-crash-worker")
        {
            await ManagedCatalogCompactionProbe.RunAsync(args[1]);
        }
        else
        {
            await ManagedReclamationProbe.RunAsync(args[0], args[1]);
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length == 2 && args[0] == "--verify-query-dataset")
{
    try
    {
        await QueryDatasetVerification.RunAsync(args[1]);
    }
    catch (Exception exception)
    {
        // A rejected oracle is an ordinary test failure, not a crash probe.
        // Exit normally with failure instead of triggering an OS core dump.
        Console.Error.WriteLine(exception);
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length == 2 && args[0] == "--maintenance-crash-worker")
{
    await MaintenanceCrashProbe.RunAsync(args[1]);
    return;
}

if (args.Length == 2 && args[0] == "--restore-crash-worker")
{
    await RestoreCrashProbe.RunAsync(args[1]);
    return;
}

if (args.Length == 3 && args[0] == "--storage-fault-worker")
{
    await StorageFaultProbe.RunAsync(args[1], args[2]);
    return;
}

if (args.Length == 2 && args[0] == "--sort-crash-worker")
{
    using var scratch = QuerySortScratch.Create(args[1]);
    File.WriteAllBytes(Path.Combine(scratch.DirectoryPath, Guid.NewGuid().ToString("N") + ".run"), new byte[8192]);
    Console.WriteLine(scratch.DirectoryPath);
    Console.Out.Flush();
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

if (args.Length != 2 || args[0] != "--crash-worker")
{
    throw new ArgumentException("Expected --crash-worker and an oracle file path.");
}
string oracle = Path.GetFullPath(args[1]);
var scenario = JsonSerializer.Deserialize<RecoveryScenario>(File.ReadAllText(oracle))!;
if (scenario.Boundary is < 0 or > 6)
{
    throw new ArgumentOutOfRangeException(nameof(scenario.Boundary));
}
string directory = Path.Combine(Path.GetDirectoryName(oracle)!, "database");
var observer = new KillBoundary(scenario.Boundary);
if (scenario.Rdf)
{
    await using var database = await RdfDatabase.OpenLocalAsync(new(directory), observer, observer);
    observer.Armed = true;
    await database.CommitAsync(scenario.RdfTransaction());
    if (scenario.Boundary == 6)
    {
        observer.Stop(); // Acknowledgement returned to the application, before disposal.
    }
    await database.CreateCheckpointAsync();
}
else
{
    await using var database = await GraphDatabase.OpenLocalAsync(new(directory), observer, observer);
    observer.Armed = true;
    await database.CommitAsync(scenario.GraphTransaction());
    if (scenario.Boundary == 6)
    {
        observer.Stop();
    }
    await database.CreateCheckpointAsync();
}
throw new InvalidOperationException("Selected crash boundary was not reached.");

sealed class KillBoundary(int boundary) : IRedoDurabilityObserver, IDatabaseCheckpointPublicationObserver
{
    internal bool Armed;

    public void Reached(RedoDurabilityStage stage)
    {
        if (Armed && boundary < 4 && (int)stage == boundary + 1)
        {
            Stop();
        }
    }

    public void Reached(DatabaseCheckpointPublicationStage stage)
    {
        if (Armed && boundary is 4 or 5 && (int)stage == boundary - 3)
        {
            Stop();
        }
    }

    internal void Stop()
    {
        Console.WriteLine("READY " + boundary);
        Console.Out.Flush();
        // The parent kills this process while it is blocked. EOF also prevents an
        // orphaned child if the parent unexpectedly dies instead.
        Console.ReadLine();
        throw new IOException("Crash controller disconnected without terminating the worker.");
    }
}
