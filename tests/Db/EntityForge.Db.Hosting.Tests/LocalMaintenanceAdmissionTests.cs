using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class LocalMaintenanceAdmissionTests
{
    private readonly TemporaryTestDirectories _directories = new("entityforge-maintenance-admission");

    [TestCleanup]
    public void Cleanup() => _directories.Cleanup();

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Retention_excludes_new_snapshot_leases_until_publication_finishes(bool rdf)
    {
        using var barrier = new CheckpointBarrier();
        if (rdf)
        {
            await using var database = await RdfDatabase.OpenLocalAsync(new(_directories.Create()), null, barrier);
            var quad = new RdfQuad(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("before"));
            await database.CommitAsync(new(Guid.CreateVersion7(), [new AssertRdfQuad(quad)]));
            await CheckAdmission(database, () => database.AcquireSnapshot(), barrier);
            await database.CommitAsync(new(Guid.CreateVersion7(), [new RetractRdfQuad(quad)]));
            Assert.HasCount(0, database.Find(new()));
        }
        else
        {
            await using var database = await GraphDatabase.OpenLocalAsync(new(_directories.Create()), null, barrier);
            var vertex = GraphVertexId.New();
            await database.CommitAsync(new(Guid.CreateVersion7(), [new CreateVertex(vertex, ["Before"])]));
            await CheckAdmission(database, () => database.AcquireSnapshot(), barrier);
            await database.CommitAsync(new(Guid.CreateVersion7(), [new DeleteVertex(vertex)]));
            Assert.IsNull(database.GetVertex(vertex));
        }
    }

    private static async Task CheckAdmission(object database, Func<IDisposable> acquire, CheckpointBarrier barrier)
    {
        barrier.Armed = true;
        Task maintenance = Task.Run(async () => await ((IDatabaseOperationRetention)database)
            .RetainOperationsAsync(DateTimeOffset.UnixEpoch.AddMilliseconds(1)));
        Exception? rejection = null;
        try
        {
            await barrier.ReachedPublication.Task.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                using var unexpected = acquire();
            }
            catch (Exception exception)
            {
                rejection = exception;
            }
        }
        finally
        {
            barrier.Release.Set();
            await maintenance.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.IsNotNull(rejection, "A snapshot entered between checkpoint capture and physical maintenance.");
        Assert.IsTrue(rejection is GraphSnapshotLeaseException or InvalidOperationException,
            "Admission must reject with the documented snapshot/maintenance error.");
        using var resumed = acquire();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Existing_snapshot_defers_retention_without_poisoning_reads_or_writes(bool rdf)
    {
        if (rdf)
        {
            await using var database = await RdfDatabase.OpenLocalAsync(new(_directories.Create()));
            var oldQuad = new RdfQuad(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("old"));
            var newQuad = new RdfQuad(oldQuad.Subject, oldQuad.Predicate, new RdfLiteral("new"));
            await database.CommitAsync(new(Guid.CreateVersion7(), [new AssertRdfQuad(oldQuad)]));
            using (var snapshot = database.AcquireSnapshot())
            {
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await ((IDatabaseOperationRetention)database).RetainOperationsAsync(DateTimeOffset.UnixEpoch.AddMilliseconds(1)));
                await database.CommitAsync(new(Guid.CreateVersion7(), [new RetractRdfQuad(oldQuad), new AssertRdfQuad(newQuad)]));
                CollectionAssert.AreEquivalent(new[] { oldQuad }, database.Find(new(), snapshot).ToArray());
                CollectionAssert.AreEquivalent(new[] { newQuad }, database.Find(new()).ToArray());
            }
            await ((IDatabaseOperationRetention)database).RetainOperationsAsync(DateTimeOffset.UnixEpoch.AddMilliseconds(1));
            CollectionAssert.AreEquivalent(new[] { newQuad }, database.Find(new()).ToArray());
        }
        else
        {
            await using var database = await GraphDatabase.OpenLocalAsync(new(_directories.Create()));
            var vertex = GraphVertexId.New();
            await database.CommitAsync(new(Guid.CreateVersion7(), [new CreateVertex(vertex, ["Node"]),
                new SetVertexProperty(vertex, "value", new DatabaseValue.String("old"))]));
            using (var snapshot = database.AcquireSnapshot())
            {
                await Assert.ThrowsAsync<GraphSnapshotLeaseException>(async () =>
                    await ((IDatabaseOperationRetention)database).RetainOperationsAsync(DateTimeOffset.UnixEpoch.AddMilliseconds(1)));
                await database.CommitAsync(new(Guid.CreateVersion7(), [new SetVertexProperty(vertex, "value", new DatabaseValue.String("new"))]));
                Assert.AreEqual(new DatabaseValue.String("old"), database.GetVertex(vertex, snapshot.Version)!.Properties["value"]);
                Assert.AreEqual(new DatabaseValue.String("new"), database.GetVertex(vertex)!.Properties["value"]);
            }
            await ((IDatabaseOperationRetention)database).RetainOperationsAsync(DateTimeOffset.UnixEpoch.AddMilliseconds(1));
            Assert.AreEqual(new DatabaseValue.String("new"), database.GetVertex(vertex)!.Properties["value"]);
        }
    }

    private sealed class CheckpointBarrier : IDatabaseCheckpointPublicationObserver, IDisposable
    {
        internal bool Armed;
        internal readonly TaskCompletionSource ReachedPublication = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();

        public void Reached(DatabaseCheckpointPublicationStage stage)
        {
            if (!Armed || stage != DatabaseCheckpointPublicationStage.BeforeReplacement)
            {
                return;
            }
            Armed = false;
            ReachedPublication.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("Test did not release checkpoint publication.");
            }
        }

        public void Dispose() => Release.Dispose();
    }
}
