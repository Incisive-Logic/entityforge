using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using EntityForge.Db.Recovery.Worker;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class ProcessCrashRecoveryTests
{
    [TestMethod]
    [DataRow(false, "full", false)]
    [DataRow(true, "full", false)]
    [DataRow(false, "readonly", false)]
    [DataRow(true, "readonly", false)]
    [DataRow(false, "checkpoint-full", false)]
    [DataRow(true, "checkpoint-full", false)]
    [DataRow(false, "full", true)]
    [DataRow(true, "full", true)]
    [DataRow(false, "readonly", true)]
    [DataRow(true, "readonly", true)]
    [DataRow(false, "checkpoint-full", true)]
    [DataRow(true, "checkpoint-full", true)]
    public async Task Native_profiles_preserve_acknowledged_data_after_filesystem_errors(bool rdf, string mode, bool largeValues)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("ENTITYFORGE_FILESYSTEM_FAULTS") != "1")
        {
            Assert.Inconclusive("Opt in with ENTITYFORGE_FILESYSTEM_FAULTS=1 on Linux with private mount namespace privileges.");
        }
        string root = _directories.Create();
        var random = new Random(3907511);
        string Payload()
        {
            var bytes = new byte[largeValues ? 16 * 1024 : 512];
            random.NextBytes(bytes);
            return Convert.ToHexString(bytes);
        }
        int count = largeValues ? 16 : 128;
        var scenario = new RecoveryScenario(rdf, mode is "checkpoint-full" or "readonly" ? 6 : -1, Guid.NewGuid(), Guid.NewGuid(),
            Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray(),
            Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray(),
            Enumerable.Range(0, count).Select(_ => Payload()).ToArray());
        string oracle = Path.Combine(root, "oracle.json");
        using (var stream = new FileStream(oracle, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, scenario);
            stream.Flush(flushToDisk: true);
        }
        string dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"));
        var start = new ProcessStartInfo("unshare")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        string[] arguments = ["--mount", "--propagation", "private", dotnet,
            typeof(RecoveryScenario).Assembly.Location, "--storage-fault-worker", oracle, mode];
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        start.Environment["COMPlus_DbgEnableMiniDump"] = "0";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            Assert.AreEqual(0, process.ExitCode, await errors + await output);
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
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "fault.json")));
        Assert.AreEqual(mode == "full" ? "commit" : mode == "checkpoint-full" ? "checkpoint" : "open",
            report.RootElement.GetProperty("phase").GetString());
        // The parent reopens the closed file copy and checks the prewritten oracle.
        // Shared verification checks every payload/edge and idempotent retry after recovery.
        var expected = JsonSerializer.Deserialize<RecoveryScenario>(await File.ReadAllTextAsync(oracle))!;
        string recovered = Path.Combine(root, "recovered");
        if (rdf)
        {
            await VerifyRdf(recovered, expected);
        }
        else
        {
            await VerifyGraph(recovered, expected);
        }
        Console.WriteLine($"{(rdf ? "RDF" : "Graph")} {mode}: {report.RootElement.GetProperty("error").GetString()}");
    }
}
