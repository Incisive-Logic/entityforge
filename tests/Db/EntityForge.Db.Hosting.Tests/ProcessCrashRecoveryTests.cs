using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Recovery.Worker;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed partial class ProcessCrashRecoveryTests
{
    private readonly TemporaryTestDirectories _directories = new("entityforge-process-recovery");

    [TestCleanup]
    public void Cleanup() => _directories.Cleanup();

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Abrupt_process_death_preserves_committed_data_and_transaction_atomicity(bool rdf, bool largeValues)
    {
        int trials = Int32.Parse(Environment.GetEnvironmentVariable("ENTITYFORGE_PROCESS_CRASH_TRIALS") ?? "7", CultureInfo.InvariantCulture);
        Assert.IsTrue(trials is >= 1 and <= 1000);
        var random = new Random(rdf ? 1107512 : 3907525);
        for (int trial = 0; trial < trials; trial++)
        {
            string directory = _directories.Create();
            int count = random.Next(2, 9);
            Guid NextId()
            {
                byte[] bytes = new byte[16];
                random.NextBytes(bytes);
                return new(bytes);
            }
            var scenario = new RecoveryScenario(rdf, trial % 7, NextId(), NextId(),
                Enumerable.Range(0, count).Select(_ => NextId()).ToArray(),
                Enumerable.Range(0, count).Select(_ => NextId()).ToArray(),
                Enumerable.Range(0, count).Select(_ => Payload()).ToArray());
            string Payload()
            {
                if (!largeValues)
                {
                    return new string('x', random.Next(1, 1025));
                }
                byte[] bytes = new byte[random.Next(16 * 1024, 64 * 1024)];
                random.NextBytes(bytes);
                return Convert.ToHexString(bytes);
            }
            string databasePath = Path.Combine(directory, "database");
            try
            {
                if (rdf)
                {
                    await using var database = await RdfDatabase.OpenLocalAsync(new(databasePath));
                    await database.CommitAsync(new(NextId(), [new AssertRdfQuad(scenario.AnchorQuad())]));
                    await database.CreateCheckpointAsync();
                }
                else
                {
                    await using var database = await GraphDatabase.OpenLocalAsync(new(databasePath));
                    await database.CommitAsync(new(NextId(), [new CreateVertex(new(scenario.Anchor), ["Acknowledged"])]));
                    await database.CreateCheckpointAsync();
                }
                string oracle = Path.Combine(directory, "oracle.json");
                using (var file = new FileStream(oracle, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, scenario);
                    file.Flush(flushToDisk: true);
                }
                await KillAtBoundary(oracle, scenario.Boundary);
                // Deserialize the independent pre-crash oracle, not the worker's
                // interpretation of what it managed to write.
                var expected = JsonSerializer.Deserialize<RecoveryScenario>(File.ReadAllText(oracle))!;
                if (rdf)
                {
                    await VerifyRdf(databasePath, expected);
                }
                else
                {
                    await VerifyGraph(databasePath, expected);
                }
            }
            catch (Exception exception)
            {
                throw new AssertFailedException($"Profile {(rdf ? "RDF" : "Graph")}, large={largeValues}, trial {trial}, boundary {scenario.Boundary}: {exception}");
            }
            finally
            {
                // Only this test's freshly allocated directory is removed, after
                // the owned process and reopened database have both stopped.
                Directory.Delete(directory, recursive: true);
            }
        }
        Console.WriteLine($"Abrupt process recovery {(rdf ? "RDF" : "Graph")}: {trials} trials passed across seven boundaries. Process termination is not simulated power loss.");
    }

    private static async Task KillAtBoundary(string oracle, int boundary, string mode = "--crash-worker")
    {
        string dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(RecoveryScenario).Assembly.Location);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(oracle);
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        start.Environment["COMPlus_DbgEnableMiniDump"] = "0";
        using var process = Process.Start(start)!;
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            string? ready = await process.StandardOutput.ReadLineAsync(deadline.Token);
            if (ready != "READY " + boundary)
            {
                await process.WaitForExitAsync(deadline.Token);
                Assert.Fail($"Worker did not reach boundary {boundary}: {ready}; {await errors}");
            }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(deadline.Token);
            Assert.AreNotEqual(0, process.ExitCode, "Worker unexpectedly shut down normally.");
            Assert.AreEqual("", await errors, "Worker reported an unexpected error before termination.");
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

    private static void VerifyPresence(RecoveryScenario scenario, int present)
    {
        Assert.IsTrue(present == 0 || present == scenario.Vertices.Length, "Partially recovered transaction.");
        if (scenario.Boundary >= 3)
        {
            Assert.AreEqual(scenario.Vertices.Length, present, "Durably flushed or acknowledged transaction was lost.");
        }
        if (scenario.Boundary == 0)
        {
            Assert.AreEqual(0, present, "Transaction appeared before its WAL write.");
        }
    }

    private static async Task VerifyGraph(string directory, RecoveryScenario scenario)
    {
        await using var database = await GraphDatabase.OpenLocalAsync(new(directory));
        var anchor = new GraphVertexId(scenario.Anchor);
        Assert.IsNotNull(database.GetVertex(anchor), "Previously acknowledged anchor was lost.");
        int present = scenario.Vertices.Count(id => database.GetVertex(new(id)) is not null);
        VerifyPresence(scenario, present);
        Assert.AreEqual(present, scenario.Edges.Count(id => database.GetEdge(new(id)) is not null));
        Assert.AreEqual(present, database.Outgoing(anchor).Count);
        if (present > 0)
        {
            for (int i = 0; i < scenario.Vertices.Length; i++)
            {
                var vertex = new GraphVertexId(scenario.Vertices[i]);
                Assert.AreEqual(new DatabaseValue.String(scenario.Payloads[i]), database.GetVertex(vertex)!.Properties["payload"]);
                CollectionAssert.AreEquivalent(new[] { "Recovery" }, database.GetVertex(vertex)!.Labels.ToArray());
                var edge = database.GetEdge(new(scenario.Edges[i]))!;
                Assert.AreEqual(anchor, edge.From);
                Assert.AreEqual(vertex, edge.To);
                Assert.AreEqual("link", edge.Label);
                Assert.AreEqual(1, database.Incoming(vertex).Count);
            }
        }
        var transaction = scenario.GraphTransaction();
        var version = await database.CommitAsync(transaction);
        Assert.AreEqual(2L, version.Value);
        Assert.AreEqual(version, await database.CommitAsync(transaction), "Retry duplicated the recovered transaction.");
        Assert.AreEqual(scenario.Vertices.Length, database.Outgoing(anchor).Count);
    }

    private static async Task VerifyRdf(string directory, RecoveryScenario scenario)
    {
        await using var database = await RdfDatabase.OpenLocalAsync(new(directory));
        var stored = database.Find(new()).ToHashSet();
        Assert.IsTrue(stored.Contains(scenario.AnchorQuad()), "Previously acknowledged anchor was lost.");
        var quads = scenario.Quads();
        int present = quads.Count(stored.Contains);
        VerifyPresence(scenario, present);
        Assert.AreEqual(present + 1, stored.Count);
        var transaction = scenario.RdfTransaction();
        var version = await database.CommitAsync(transaction);
        Assert.AreEqual(2L, version.Value);
        Assert.AreEqual(version, await database.CommitAsync(transaction), "Retry duplicated the recovered transaction.");
        CollectionAssert.AreEquivalent(quads.Append(scenario.AnchorQuad()).ToArray(), database.Find(new()).ToArray());
    }
}
