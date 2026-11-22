using System.Diagnostics;
using System.Runtime.InteropServices;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class BackupRecoveryEvidenceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Server_backup_commands_restore_large_values_and_reject_invalid_destinations(bool rdf)
    {
        string source = At("cli-source"), restored = At("cli-restored"), artifact = At("cli.backup");
        string commandDataDirectory = source;
        string payload = new string('p', 128 * 1024);
        var vertex = GraphVertexId.New();
        var quad = new RdfQuad(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral(payload));
        if (rdf)
        {
            await using var database = await RdfDatabase.OpenLocalAsync(new(source));
            await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(quad)]));
        }
        else
        {
            await using var database = await GraphDatabase.OpenLocalAsync(new(source));
            await database.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(vertex, ["Node"]),
                new SetVertexProperty(vertex, "payload", new DatabaseValue.String(payload))]));
        }
        await Run(0, "backup", artifact);
        await Run(0, "verify-backup", artifact);
        string legacyStage = restored + ".restore.new";
        Directory.CreateDirectory(legacyStage);
        await File.WriteAllTextAsync(Path.Combine(legacyStage, "sentinel"), "other attempt");
        await Run(0, "restore", artifact, restored);
        Assert.AreEqual("other attempt", await File.ReadAllTextAsync(Path.Combine(legacyStage, "sentinel")));
        if (rdf)
        {
            await using var database = await RdfDatabase.OpenLocalAsync(new(restored));
            Assert.AreEqual(quad, database.Find(new()).Single());
        }
        else
        {
            await using var database = await GraphDatabase.OpenLocalAsync(new(restored));
            Assert.AreEqual(new DatabaseValue.String(payload), database.GetVertex(vertex)!.Properties["payload"]);
        }
        string sentinel = Path.Combine(restored, "operator-sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "preserve");
        await Run(1, "restore", artifact, restored);
        Assert.AreEqual("preserve", await File.ReadAllTextAsync(sentinel));
        await Run(1, "restore", artifact);
        string corrupt = At("corrupt.backup");
        await File.WriteAllTextAsync(corrupt, "invalid backup");
        await Run(1, "verify-backup", corrupt);
        await Run(1, "restore", corrupt, At("invalid-target"));
        Assert.IsFalse(Directory.Exists(At("invalid-target")));
        Directory.CreateDirectory(At("existing-empty"));
        await Run(1, "restore", corrupt, At("existing-empty"));
        Assert.IsTrue(Directory.Exists(At("existing-empty")));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(At("existing-empty")).Any());
        if (rdf)
        {
            await using var owner = await RdfDatabase.OpenLocalAsync(new(source));
            await Run(1, "backup", At("owned-source.backup"));
            Assert.AreEqual(quad, owner.Find(new()).Single());
        }
        else
        {
            await using var owner = await GraphDatabase.OpenLocalAsync(new(source));
            await Run(1, "backup", At("owned-source.backup"));
            Assert.AreEqual(new DatabaseValue.String(payload), owner.GetVertex(vertex)!.Properties["payload"]);
        }
        Assert.IsFalse(File.Exists(At("owned-source.backup")));
        commandDataDirectory = At("missing-source");
        await Run(1, "backup", At("missing-source.backup"));
        Assert.IsFalse(Directory.Exists(commandDataDirectory));
        Assert.IsFalse(File.Exists(At("missing-source.backup")));
        Directory.CreateDirectory(commandDataDirectory);
        await Run(1, "backup", At("empty-source.backup"));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(commandDataDirectory).Any());
        Assert.IsFalse(File.Exists(At("empty-source.backup")));

        async Task Run(int expectedExit, params string[] arguments)
        {
            string dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            var start = new ProcessStartInfo(dotnet)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "EntityForge.Db.Server.dll"));
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
            foreach (string name in start.Environment.Keys.Where(name => name.StartsWith("ENTITYFORGE_", StringComparison.Ordinal)).ToArray())
            {
                start.Environment.Remove(name);
            }
            start.Environment["ENTITYFORGE_DB_DATA_DIRECTORY"] = commandDataDirectory;
            start.Environment["ENTITYFORGE_DB_PROFILE"] = rdf ? "rdf" : "property-graph";
            // Kestrel validates named endpoints when it starts. An offline
            // command must succeed without configuring or opening a listener.
            start.Environment["Kestrel__Endpoints__OfflineBackup__Url"] = "http://127.0.0.1:not-a-port";
            start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
            start.Environment["COMPlus_DbgEnableMiniDump"] = "0";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                Assert.AreEqual(expectedExit, process.ExitCode, await output + await error);
                if (expectedExit != 0)
                {
                    StringAssert.Contains(await error, "Command failed:");
                }
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
    }
}
