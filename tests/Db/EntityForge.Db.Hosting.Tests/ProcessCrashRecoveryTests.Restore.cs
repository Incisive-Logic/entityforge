using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Recovery.Worker;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class ProcessCrashRecoveryTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Restore_process_death_preserves_backup_and_publishes_all_or_nothing(bool rdf, bool largeValues)
    {
        int trials = Int32.Parse(Environment.GetEnvironmentVariable("ENTITYFORGE_RESTORE_CRASH_TRIALS") ?? "4", CultureInfo.InvariantCulture);
        Assert.IsTrue(trials is >= 2 and <= 1000);
        var random = new Random(rdf ? 1107513 : 3907526);
        for (int trial = 0; trial < trials; trial++)
        {
            string root = _directories.Create();
            Guid NextId()
            {
                byte[] bytes = new byte[16];
                random.NextBytes(bytes);
                return new(bytes);
            }
            string Payload()
            {
                byte[] bytes = new byte[largeValues ? random.Next(16 * 1024, 64 * 1024) : random.Next(1, 513)];
                random.NextBytes(bytes);
                return Convert.ToHexString(bytes);
            }
            int count = random.Next(2, 9);
            var scenario = new RecoveryScenario(rdf, trial % 2, NextId(), NextId(),
                Enumerable.Range(0, count).Select(_ => NextId()).ToArray(),
                Enumerable.Range(0, count).Select(_ => NextId()).ToArray(),
                Enumerable.Range(0, count).Select(_ => Payload()).ToArray());
            string source = Path.Combine(root, "database"), target = Path.Combine(root, "restored"),
                backup = Path.Combine(root, "backup.efbk"), oracle = Path.Combine(root, "oracle.json");
            try
            {
                if (rdf)
                {
                    await using var db = await RdfDatabase.OpenLocalAsync(new(source));
                    await db.CommitAsync(new(NextId(), [new AssertRdfQuad(scenario.AnchorQuad())]));
                    await db.CommitAsync(scenario.RdfTransaction());
                    await db.CreateCheckpointAsync();
                }
                else
                {
                    await using var db = await GraphDatabase.OpenLocalAsync(new(source));
                    await db.CommitAsync(new(NextId(), [new CreateVertex(new(scenario.Anchor), ["Acknowledged"])]));
                    await db.CommitAsync(scenario.GraphTransaction());
                    await db.CreateCheckpointAsync();
                }
                using (var file = new FileStream(oracle, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, scenario);
                    file.Flush(flushToDisk: true);
                }
                var manager = new LocalDatabaseBackupManager();
                await manager.CreateAsync(Path.Combine(source, LocalGraphDatabaseOptions.DefaultCheckpointFileName),
                    Path.Combine(source, LocalGraphDatabaseOptions.DefaultChangeJournalDirectoryName),
                    Path.Combine(source, LocalGraphDatabaseOptions.DefaultPageFileName), backup);
                byte[] digest = SHA256.HashData(File.ReadAllBytes(backup));
                await KillAtBoundary(oracle, scenario.Boundary, "--restore-crash-worker");
                var expected = JsonSerializer.Deserialize<RecoveryScenario>(File.ReadAllText(oracle))!;
                string[] staging = Directory.GetDirectories(root, "restored.restore-*.new");
                if (scenario.Boundary == 0)
                {
                    Assert.IsFalse(Directory.Exists(target), "Incomplete restore was published.");
                    Assert.HasCount(1, staging, "Pre-publication crash must leave its owned staging directory.");
                    await RestoreCrashProbe.RestoreAsync(manager, backup, target);
                    Assert.IsTrue(Directory.Exists(staging[0]), "Retry must not remove a different restore's staging evidence.");
                }
                else
                {
                    Assert.IsTrue(Directory.Exists(target));
                    Assert.HasCount(0, staging);
                }
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await RestoreCrashProbe.RestoreAsync(manager, backup, target));
                // Both publication boundaries restore an already acknowledged version 2.
                expected = expected with
                {
                    Boundary = 6
                };
                if (rdf)
                {
                    await VerifyRdf(target, expected);
                }
                else
                {
                    await VerifyGraph(target, expected);
                }
                CollectionAssert.AreEqual(digest, SHA256.HashData(File.ReadAllBytes(backup)));
            }
            catch (Exception exception)
            {
                throw new AssertFailedException($"Restore profile RDF={rdf}, large={largeValues}, trial={trial}, boundary={scenario.Boundary}: {exception}");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
        Console.WriteLine($"Restore process recovery RDF={rdf}, large={largeValues}: {trials} seeded trials passed across both publication boundaries; not power-loss simulation.");
    }
}
