using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Recovery.Worker;

public static class RestoreCrashProbe
{
    public static async Task RunAsync(string oracle)
    {
        var scenario = JsonSerializer.Deserialize<RecoveryScenario>(File.ReadAllText(oracle))!;
        if (scenario.Boundary is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(scenario.Boundary));
        }
        string root = Path.GetDirectoryName(Path.GetFullPath(oracle))!;
        await RestoreAsync(new(new Boundary(scenario.Boundary)), Path.Combine(root, "backup.efbk"),
            Path.Combine(root, "restored"));
        throw new InvalidOperationException("Restore crash boundary was not reached.");
    }

    // Graph and RDF use the same local artifact names and grouped redo format.
    public static ValueTask<LocalDatabaseBackupInfo> RestoreAsync(LocalDatabaseBackupManager manager,
        string backup, string target) => manager.RestoreAsync(backup, target,
        LocalGraphDatabaseOptions.DefaultCheckpointFileName, LocalGraphDatabaseOptions.DefaultCommitLogFileName,
        LocalGraphDatabaseOptions.DefaultManifestFileName, LocalGraphDatabaseOptions.DefaultChangeJournalDirectoryName,
        LocalGraphDatabaseOptions.DefaultPageFileName, new GroupedRedoRestoreLogInitializer());

    private sealed class Boundary(int boundary) : IStoragePublicationObserver
    {
        public void Reached(StoragePublicationStage stage)
        {
            var expected = boundary == 0 ? StoragePublicationStage.BeforeRestoreReplacement
                : StoragePublicationStage.AfterRestoreReplacement;
            if (stage != expected)
            {
                return;
            }
            Console.WriteLine("READY " + boundary);
            Console.Out.Flush();
            Console.ReadLine();
            throw new IOException("Restore controller disconnected without terminating worker.");
        }
    }
}
