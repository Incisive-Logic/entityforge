using System.Diagnostics;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Recovery.Worker;

// Only the opt-in parent test launches this worker, in a private mount namespace.
internal static class StorageFaultProbe
{
    internal static async Task RunAsync(string oraclePath, string mode)
    {
        if (!OperatingSystem.IsLinux() || mode is not ("full" or "readonly" or "checkpoint-full"))
        {
            throw new NotSupportedException("Filesystem fault probes require Linux and a known fault mode.");
        }
        string? currentNamespace = new FileInfo("/proc/self/ns/mnt").LinkTarget;
        if (currentNamespace is null || currentNamespace == new FileInfo("/proc/1/ns/mnt").LinkTarget)
        {
            throw new InvalidOperationException("Refusing to mount outside a private mount namespace.");
        }
        string root = Path.GetDirectoryName(Path.GetFullPath(oraclePath))!;
        var scenario = JsonSerializer.Deserialize<RecoveryScenario>(await File.ReadAllTextAsync(oraclePath))!;
        string volume = Path.Combine(root, "volume");
        string databasePath = Path.Combine(volume, "database");
        string filler = Path.Combine(volume, "filler");
        Directory.CreateDirectory(volume);
        await Command("mount", "-t", "tmpfs", "-o", "size=64m", "tmpfs", volume);
        bool armed = false;
        async Task Arm()
        {
            armed = true;
            if (mode == "readonly")
            {
                await Command("mount", "-o", "remount,ro", volume);
                return;
            }
            using var stream = new FileStream(filler, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
            byte[] block = new byte[65536];
            bool exhausted = false;
            try
            {
                for (int index = 0; index < 1025; index++)
                {
                    stream.Write(block);
                }
            }
            catch (IOException exception) when (exception.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase))
            {
                exhausted = true;
            }
            if (!exhausted)
            {
                throw new InvalidOperationException("Bounded filesystem did not report ENOSPC.");
            }
            // Leave room for a partial transaction write, but not its complete payload.
            stream.SetLength(Math.Max(0, stream.Length - 16384));
        }
        async Task Restore()
        {
            if (!armed)
            {
                return;
            }
            if (mode == "readonly")
            {
                await Command("mount", "-o", "remount,rw", volume);
            }
            else
            {
                File.Delete(filler);
            }
            armed = false;
        }
        Exception? failure = null;
        string phase = "open";
        try
        {
            if (scenario.Rdf)
            {
                await using var seed = await RdfDatabase.OpenLocalAsync(new(databasePath));
                await seed.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(scenario.AnchorQuad())]));
                if (mode == "readonly")
                {
                    await seed.CommitAsync(scenario.RdfTransaction());
                }
                await seed.CreateCheckpointAsync();
            }
            else
            {
                await using var seed = await GraphDatabase.OpenLocalAsync(new(databasePath));
                await seed.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(new(scenario.Anchor), ["Acknowledged"])]));
                if (mode == "readonly")
                {
                    await seed.CommitAsync(scenario.GraphTransaction());
                }
                await seed.CreateCheckpointAsync();
            }
            if (mode == "readonly")
            {
                await Arm();
            }
            try
            {
                if (scenario.Rdf)
                {
                    await using var database = await RdfDatabase.OpenLocalAsync(new(databasePath));
                    await Attempt(() => database.CommitAsync(scenario.RdfTransaction()).AsTask(), () => database.CreateCheckpointAsync().AsTask());
                }
                else
                {
                    await using var database = await GraphDatabase.OpenLocalAsync(new(databasePath));
                    await Attempt(() => database.CommitAsync(scenario.GraphTransaction()).AsTask(), () => database.CreateCheckpointAsync().AsTask());
                }
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                await Restore();
            }
            if (failure is null || !failure.ToString().Contains(mode == "readonly" ? "Read-only file system" : "No space left", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Expected filesystem error was not observed.", failure);
            }
            // Copy only the closed database, never the filler, for parent-side verification.
            string recovered = Path.Combine(root, "recovered");
            Directory.CreateDirectory(recovered);
            long copied = 0;
            foreach (string file in Directory.EnumerateFiles(databasePath, "*", SearchOption.AllDirectories))
            {
                copied += new FileInfo(file).Length;
                if (copied > 64 * 1024 * 1024)
                {
                    throw new InvalidOperationException("Recovery evidence copy exceeds its bound.");
                }
                string target = Path.Combine(recovered, Path.GetRelativePath(databasePath, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            await File.WriteAllTextAsync(Path.Combine(root, "fault.json"), JsonSerializer.Serialize(new
            {
                mode,
                phase,
                error = failure.ToString(),
                copied
            }));
        }
        finally
        {
            await Restore();
            await Command("umount", volume);
        }

        async Task Attempt(Func<Task<DatabaseVersion>> commit, Func<Task<DatabaseVersion>> checkpoint)
        {
            if (mode == "checkpoint-full")
            {
                await commit();
                await Arm();
                phase = "checkpoint";
                try
                {
                    await checkpoint();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    await Restore();
                }
                return;
            }
            if (mode == "full")
            {
                await Arm();
            }
            phase = "commit";
            try
            {
                await commit();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                await Restore();
            }
        }
    }

    private static async Task Command(string command, params string[] arguments)
    {
        var start = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new IOException(command + " failed: " + error);
        }
    }
}
