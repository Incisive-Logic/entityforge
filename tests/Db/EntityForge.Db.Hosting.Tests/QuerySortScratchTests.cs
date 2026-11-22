using System.Diagnostics;
using System.Runtime.InteropServices;
using EntityForge.Db.Query.Planning;
using EntityForge.Db.Recovery.Worker;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class QuerySortScratchTests
{
    private readonly TemporaryTestDirectories _directories = new("entityforge-sort-recovery-tests");

    [TestCleanup]
    public void Cleanup() => _directories.Cleanup();

    private string CreateRoot()
    {
        string root = _directories.Create();
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return root;
    }

    [TestMethod]
    public async Task Sort_scratch_recovery_preserves_live_owner_and_recovers_after_process_death()
    {
        string root = CreateRoot();
        string dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(RecoveryScenario).Assembly.Location);
        start.ArgumentList.Add("--sort-crash-worker");
        start.ArgumentList.Add(root);
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        start.Environment["COMPlus_DbgEnableMiniDump"] = "0";
        using var process = Process.Start(start)!;
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            string? directory = await process.StandardOutput.ReadLineAsync(deadline.Token);
            Assert.IsNotNull(directory);
            Assert.AreEqual(Path.GetFullPath(root), Path.GetDirectoryName(directory));
            Assert.AreEqual(0, QuerySortScratch.Recover(root));
            Assert.AreEqual(1, Directory.GetFiles(directory, "*.run").Length);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(deadline.Token);
            Assert.AreEqual("", await errors);
            Assert.AreEqual(1, QuerySortScratch.Recover(root));
            Assert.IsFalse(Directory.Exists(directory));
            Assert.AreEqual(0, QuerySortScratch.Recover(root));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(cleanup.Token);
            }
        }
    }

    [TestMethod]
    public void Sort_scratch_recovery_preserves_unknown_directories_and_contents()
    {
        string root = CreateRoot();
        string unknown = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "owner.lock"), "unrelated application");
        File.WriteAllText(Path.Combine(unknown, Guid.NewGuid().ToString("N") + ".run"), "keep");
        string unmarked = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unmarked);
        File.WriteAllText(Path.Combine(unmarked, "database"), "keep");
        Assert.AreEqual(0, QuerySortScratch.Recover(root));
        Assert.AreEqual(2, Directory.GetFiles(unknown).Length);
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(unmarked, "database")));
    }

    [TestMethod]
    public void Sort_scratch_recovery_preserves_unknown_files_even_with_a_valid_owner_marker()
    {
        string root = CreateRoot();
        var scratch = QuerySortScratch.Create(root);
        string run = Path.Combine(scratch.DirectoryPath, Guid.NewGuid().ToString("N") + ".run");
        string unrelated = Path.Combine(scratch.DirectoryPath, "application.data");
        File.WriteAllText(run, "sort bytes");
        File.WriteAllText(unrelated, "keep");
        scratch.Dispose(); // Releases ownership but leaves runs and recovery marker.
        Assert.AreEqual(0, QuerySortScratch.Recover(root));
        Assert.AreEqual("keep", File.ReadAllText(unrelated));
        Assert.AreEqual("sort bytes", File.ReadAllText(run));
        File.Delete(unrelated); // Only this test-created file, not an automatic recovery action.
        using var replacement = QuerySortScratch.Create(root);
        Assert.IsFalse(Directory.Exists(scratch.DirectoryPath));
    }

    [TestMethod]
    public void Sort_scratch_recovery_does_not_follow_directory_links()
    {
        string root = CreateRoot();
        string target = CreateRoot();
        using var scratch = QuerySortScratch.Create(target);
        string link = Path.Combine(root, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateSymbolicLink(link, scratch.DirectoryPath);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows symbolic-link privilege unavailable; exercised on Linux.");
        }
        catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 1314)
        {
            Assert.Inconclusive("Windows symbolic-link privilege unavailable; exercised on Linux.");
        }
        try
        {
            Assert.AreEqual(0, QuerySortScratch.Recover(root));
            Assert.IsTrue(File.Exists(Path.Combine(scratch.DirectoryPath, "owner.lock")));
        }
        finally
        {
            Directory.Delete(link, recursive: false);
        }
    }
}
