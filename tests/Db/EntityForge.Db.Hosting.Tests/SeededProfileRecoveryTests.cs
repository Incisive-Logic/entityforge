using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SeededProfileRecoveryTests
{
    private readonly TemporaryTestDirectories _directories = new("entityforge-seeded-recovery");

    [TestCleanup]
    public void Cleanup() => _directories.Cleanup();

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Seeded_profile_faults_preserve_acknowledged_data_atomicity_and_replay(bool rdf)
    {
        int trials = Int32.Parse(Environment.GetEnvironmentVariable("ENTITYFORGE_RECOVERY_FAULT_TRIALS") ?? "12",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsTrue(trials is >= 1 and <= 1000, "Qualification trials must be between 1 and 1000.");
        var random = new Random(rdf ? 1107511 : 3907524);
        for (int trial = 0; trial < trials; trial++)
        {
            string directory = _directories.Create();
            int boundary = trial % 6;
            int count = random.Next(2, 9);
            var fault = new BoundaryFault(boundary);
            try
            {
                if (rdf)
                {
                    await RdfTrial(directory, fault, count, random);
                }
                else
                {
                    await GraphTrial(directory, fault, count, random);
                }
                Assert.IsTrue(fault.Fired, $"The selected boundary was not exercised: trial {trial}, boundary {boundary}.");
            }
            catch (Exception exception)
            {
                throw new AssertFailedException($"Profile {(rdf ? "RDF" : "Graph")}, trial {trial}, boundary {boundary}, count {count}: {exception}");
            }
            finally
            {
                // Each trial owns this freshly allocated directory; retain no database images.
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }
        Console.WriteLine($"Seeded {(rdf ? "RDF" : "Graph")} recovery: {trials} trials passed across six exception-injection boundaries. This is not a process-kill or power-loss test.");
    }

    private static async Task GraphTrial(string directory, BoundaryFault fault, int count, Random random)
    {
        var anchor = new GraphVertexId(NextId(random));
        var vertices = Enumerable.Range(0, count).Select(_ => new GraphVertexId(NextId(random))).ToArray();
        var edges = vertices.Select(_ => new GraphEdgeId(NextId(random))).ToArray();
        Guid operation = NextId(random);
        var mutations = new List<GraphMutation>();
        for (int i = 0; i < count; i++)
        {
            mutations.Add(new CreateVertex(vertices[i], ["Recovery"]));
            mutations.Add(new SetVertexProperty(vertices[i], "payload", new DatabaseValue.String(new string('x', random.Next(1, 1025)))));
            mutations.Add(new CreateEdge(edges[i], anchor, vertices[i], "link"));
        }
        var transaction = new GraphTransaction(operation, mutations);
        DatabaseVersion? acknowledged = null;
        await using (var database = await GraphDatabase.OpenLocalAsync(new(directory), fault, fault))
        {
            await database.CommitAsync(new(NextId(random), [new CreateVertex(anchor, ["Acknowledged"])]));
            await database.CreateCheckpointAsync();
            if (fault.Checkpoint)
            {
                acknowledged = await database.CommitAsync(transaction);
                fault.Armed = true;
                await Assert.ThrowsAsync<EntityForgeDbCheckpointException>(async () => await database.CreateCheckpointAsync());
            }
            else
            {
                fault.Armed = true;
                await Assert.ThrowsAsync<EntityForgeDbCommitException>(async () => await database.CommitAsync(transaction));
            }
        }
        await using var recovered = await GraphDatabase.OpenLocalAsync(new(directory));
        Assert.IsNotNull(recovered.GetVertex(anchor), "Previously acknowledged data was lost.");
        int present = vertices.Count(id => recovered.GetVertex(id) is not null);
        Assert.IsTrue(present == 0 || present == count, "A transaction was partially recovered.");
        Assert.AreEqual(present, edges.Count(id => recovered.GetEdge(id) is not null));
        if (present != 0)
        {
            foreach (var property in mutations.OfType<SetVertexProperty>())
            {
                Assert.AreEqual(property.Value, recovered.GetVertex(property.Vertex)!.Properties[property.Name]);
            }
        }
        if (fault.MustRecover)
        {
            Assert.AreEqual(count, present);
        }
        if (fault.BeforeWrite)
        {
            Assert.AreEqual(0, present);
        }
        var version = await recovered.CommitAsync(transaction);
        if (acknowledged is { } expected)
        {
            Assert.AreEqual(expected, version);
        }
        Assert.AreEqual(version, await recovered.CommitAsync(transaction));
        Assert.AreEqual(count, recovered.Outgoing(anchor).Count);
        foreach (var vertex in vertices)
        {
            Assert.AreEqual(1, recovered.Incoming(vertex).Count);
        }
    }

    private static async Task RdfTrial(string directory, BoundaryFault fault, int count, Random random)
    {
        var anchor = new RdfQuad(new RdfIri("urn:acknowledged"), new("urn:p"), new RdfLiteral("kept"));
        var quads = Enumerable.Range(0, count).Select(i => new RdfQuad(new RdfIri("urn:trial:" + i), new("urn:p"),
            new RdfLiteral(new string('x', random.Next(1, 1025))), i % 2 == 0 ? new RdfIri("urn:g") : null)).ToArray();
        var transaction = new RdfTransaction(NextId(random), quads.Select(quad => (RdfMutation)new AssertRdfQuad(quad)).ToArray());
        DatabaseVersion? acknowledged = null;
        await using (var database = await RdfDatabase.OpenLocalAsync(new(directory), fault, fault))
        {
            await database.CommitAsync(new(NextId(random), [new AssertRdfQuad(anchor)]));
            await database.CreateCheckpointAsync();
            if (fault.Checkpoint)
            {
                acknowledged = await database.CommitAsync(transaction);
                fault.Armed = true;
                await Assert.ThrowsAsync<EntityForgeDbCheckpointException>(async () => await database.CreateCheckpointAsync());
            }
            else
            {
                fault.Armed = true;
                await Assert.ThrowsAsync<EntityForgeDbCommitException>(async () => await database.CommitAsync(transaction));
            }
        }
        await using var recovered = await RdfDatabase.OpenLocalAsync(new(directory));
        var stored = recovered.Find(new()).ToHashSet();
        Assert.IsTrue(stored.Contains(anchor), "Previously acknowledged data was lost.");
        int present = quads.Count(stored.Contains);
        Assert.IsTrue(present == 0 || present == count, "A transaction was partially recovered.");
        if (fault.MustRecover)
        {
            Assert.AreEqual(count, present);
        }
        if (fault.BeforeWrite)
        {
            Assert.AreEqual(0, present);
        }
        var version = await recovered.CommitAsync(transaction);
        if (acknowledged is { } expected)
        {
            Assert.AreEqual(expected, version);
        }
        Assert.AreEqual(version, await recovered.CommitAsync(transaction));
        CollectionAssert.AreEquivalent(quads.Append(anchor).ToArray(), recovered.Find(new()).ToArray());
    }

    private static Guid NextId(Random random)
    {
        Span<byte> bytes = stackalloc byte[16];
        random.NextBytes(bytes);
        return new(bytes);
    }

    private sealed class BoundaryFault(int boundary) : IRedoDurabilityObserver, IDatabaseCheckpointPublicationObserver
    {
        internal bool Armed;
        internal bool Fired;

        internal bool Checkpoint => boundary >= 4;

        internal bool MustRecover => boundary >= 3;

        internal bool BeforeWrite => boundary == 0;

        public void Reached(RedoDurabilityStage stage)
        {
            if (!Checkpoint && Armed && (int)stage == boundary + 1)
            {
                Fire();
            }
        }

        public void Reached(DatabaseCheckpointPublicationStage stage)
        {
            if (Checkpoint && Armed && (int)stage == boundary - 3)
            {
                Fire();
            }
        }

        private void Fire()
        {
            Armed = false;
            Fired = true;
            throw new IOException("Injected durability boundary interruption.");
        }
    }
}
