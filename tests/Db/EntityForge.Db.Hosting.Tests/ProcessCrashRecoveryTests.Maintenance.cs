using System.Globalization;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Recovery.Worker;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class ProcessCrashRecoveryTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Maintenance_process_death_preserves_acknowledged_state_and_tombstones(bool rdf, bool largeValues)
    {
        int trials = Int32.Parse(Environment.GetEnvironmentVariable("ENTITYFORGE_MAINTENANCE_CRASH_TRIALS") ?? "4", CultureInfo.InvariantCulture);
        Assert.IsTrue(trials is >= 4 and <= 1000);
        var random = new Random(rdf ? 1107514 : 3907527);
        for (int trial = 0; trial < trials; trial++)
        {
            string root = _directories.Create(), source = Path.Combine(root, "database"), oracle = Path.Combine(root, "oracle.json");
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
            // Retention requires timestamp-bearing retry identities; entity IDs
            // and payloads remain independently generated from the fixed seed.
            var scenario = new RecoveryScenario(rdf, trial % 4, NextId(), Guid.CreateVersion7(),
                Enumerable.Range(0, count).Select(_ => NextId()).ToArray(),
                Enumerable.Range(0, count).Select(_ => NextId()).ToArray(),
                Enumerable.Range(0, count).Select(_ => Payload()).ToArray());
            // Vertex and edge identities have separate domains. Reusing an oracle
            // edge ID here makes the deleted vertex check durable as well.
            Guid removed = scenario.Edges[0];
            try
            {
                // Multiple individually acknowledged runs contain both live data
                // and tombstones before the child requests physical maintenance.
                if (rdf)
                {
                    await using var database = await RdfDatabase.OpenLocalAsync(new(source));
                    await database.CommitAsync(new(NextId(), [new AssertRdfQuad(scenario.AnchorQuad())]));
                    await database.CommitAsync(scenario.RdfTransaction());
                    for (int index = 0; index < 4; index++)
                    {
                        var quad = new RdfQuad(new RdfIri("urn:removed:" + removed), new("urn:p"), new RdfLiteral(Payload()));
                        await database.CommitAsync(new(NextId(), [new AssertRdfQuad(quad)]));
                        await database.CommitAsync(new(NextId(), [new RetractRdfQuad(quad)]));
                    }
                }
                else
                {
                    await using var database = await GraphDatabase.OpenLocalAsync(new(source));
                    await database.CommitAsync(new(NextId(), [new CreateVertex(new(scenario.Anchor), ["Acknowledged"])]));
                    await database.CommitAsync(scenario.GraphTransaction());
                    for (int index = 0; index < 4; index++)
                    {
                        await database.CommitAsync(new(NextId(), [new CreateVertex(new(removed), ["Removed"]),
                            new SetVertexProperty(new(removed), "payload", new DatabaseValue.String(Payload()))]));
                        await database.CommitAsync(new(NextId(), [new DeleteVertex(new(removed))]));
                    }
                }
                using (var file = new FileStream(oracle, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, scenario);
                    file.Flush(flushToDisk: true);
                }
                await KillAtBoundary(oracle, scenario.Boundary, "--maintenance-crash-worker");
                var expected = JsonSerializer.Deserialize<RecoveryScenario>(File.ReadAllText(oracle))! with
                {
                    Boundary = 6
                };
                if (rdf)
                {
                    await VerifyRdf(source, expected);
                }
                else
                {
                    await VerifyGraph(source, expected);
                    await using var database = await GraphDatabase.OpenLocalAsync(new(source));
                    Assert.IsNull(database.GetVertex(new(expected.Edges[0])), "Compaction resurrected a deleted vertex.");
                }
            }
            catch (Exception exception)
            {
                throw new AssertFailedException($"Maintenance RDF={rdf}, large={largeValues}, trial={trial}, boundary={scenario.Boundary}: {exception}");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
        Console.WriteLine($"Maintenance process recovery RDF={rdf}, large={largeValues}: {trials} seeded trials passed across redo/publish/flush/trim; not power-loss simulation.");
    }
}
