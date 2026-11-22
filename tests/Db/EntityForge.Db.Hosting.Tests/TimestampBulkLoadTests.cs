using EntityForge.Db.Durability;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class TimestampBulkLoadTests
{
    private sealed class Clock : IDatabaseBoundedClock
    {
        public Guid Domain { get; } = Guid.NewGuid();

        public DatabaseClockInterval Read()
        {
            long now = DateTime.UtcNow.Ticks;
            return new(now, now);
        }
    }

    [TestMethod]
    public async Task Graph_bulk_reindex_and_reopen_preserve_timestamp_views()
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-clock-bulk-graph-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock();
        var id = GraphVertexId.New();
        Guid operation = Guid.NewGuid();
        var vertex = new GraphVertex(id, new HashSet<string>(), new Dictionary<string, DatabaseValue>());
        try
        {
            await using (var graph = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = clock
            }))
            {
                var kernel = ((GraphDatabaseEngine)graph).PartitionKernel;
                using var pin = await kernel.PartitionTransactions.CaptureSnapshotAsync();
                var before = DatabaseTimestampReadClock.Read(clock.Domain, clock);
                await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(pin, before, null);
                await graph.BulkLoadAsync(operation, [vertex], []);
                object old = await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(pin, before, null);
                using (kernel.PartitionTransactions.EnterTimestampProjection(old))
                {
                    Assert.IsNull(await graph.GetVertexAsync(id, graph.CurrentVersion));
                }

                await graph.ReindexAsync();
                object current = await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(pin, DatabaseTimestampReadClock.Read(clock.Domain, clock), null);
                using (kernel.PartitionTransactions.EnterTimestampProjection(current))
                {
                    Assert.IsNotNull(await graph.GetVertexAsync(id, graph.CurrentVersion));
                }
            }
            await using var reopened = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = clock
            });
            Assert.IsNotNull(await reopened.GetVertexAsync(id, reopened.CurrentVersion));
            await reopened.BulkLoadAsync(operation, [vertex], []);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [TestMethod]
    public async Task Rdf_bulk_reindex_and_reopen_preserve_timestamp_views()
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-clock-bulk-rdf-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock();
        Guid operation = Guid.NewGuid();
        var quad = new RdfQuad(new RdfIri("urn:subject"), new RdfIri("urn:property"), new RdfLiteral("value"));
        try
        {
            await using (var rdf = await RdfDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = clock
            }))
            {
                var kernel = ((RdfDatabaseEngine)rdf).PartitionKernel;
                using var pin = await kernel.PartitionTransactions.CaptureSnapshotAsync();
                var before = DatabaseTimestampReadClock.Read(clock.Domain, clock);
                await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(pin, before, null);
                await rdf.BulkLoadAsync(operation, [quad]);
                object old = await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(pin, before, null);
                using (kernel.PartitionTransactions.EnterTimestampProjection(old))
                {
                    Assert.AreEqual(0, rdf.Find(new()).Count);
                }

                await rdf.ReindexAsync();
                object current = await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(pin, DatabaseTimestampReadClock.Read(clock.Domain, clock), null);
                using (kernel.PartitionTransactions.EnterTimestampProjection(current))
                {
                    Assert.AreEqual(1, rdf.Find(new()).Count);
                }
            }
            await using var reopened = await RdfDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = clock
            });
            Assert.AreEqual(1, reopened.Find(new()).Count);
            await reopened.BulkLoadAsync(operation, [quad]);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [TestMethod]
    public async Task Semantic_bulk_reindex_and_reopen_preserve_timestamp_mode()
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-clock-bulk-semantic-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock();
        SemanticSessionEnvelope session;
        try
        {
            await using (var semantic = await SemanticDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = clock
            }))
            {
                var write = semantic.EntityStore<EntityOperationTransportTests.Item>().PrepareSave(new("bulk", "value"), SemanticEntityWriteOperation.Create());
                session = SemanticEntityDeliveryCodec.Session(write.Export());
                var kernel = ((SemanticProfileCommitLog)((SemanticDatabaseEngine)semantic).TransferRuntime).PartitionKernel;
                using var pin = await kernel.PartitionTransactions.CaptureSnapshotAsync();
                var before = DatabaseTimestampReadClock.Read(clock.Domain, clock);
                await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(pin, before, null);
                var field = session.Pages.SelectMany(page => page.Changes).SelectMany(change => change.Fields)
                    .First(change => change.Value is not null);
                var query = new SemanticFieldEqualityQuery(field.Field, field.Value!);
                await semantic.BulkLoadAsync(session);
                object old = await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(pin, before, null);
                using (kernel.PartitionTransactions.EnterTimestampProjection(old))
                {
                    Assert.AreEqual(0, await QueryRetainedAsync((SemanticDatabaseEngine)semantic, query));
                }

                await Assert.ThrowsExceptionAsync<EntityForgeDbStorageException>(() => semantic.ReindexAsync().AsTask());
                object current = await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(pin, DatabaseTimestampReadClock.Read(clock.Domain, clock), null);
                using (kernel.PartitionTransactions.EnterTimestampProjection(current))
                {
                    Assert.AreEqual(1, await QueryRetainedAsync((SemanticDatabaseEngine)semantic, query));
                }

                pin.Dispose();
                await semantic.ReindexAsync();
                foreach (var manifest in semantic.EntityStore<EntityOperationTransportTests.Item>().Manifests)
                {
                    await semantic.Mappings().RegisterAsync(manifest);
                }

                Assert.AreEqual("value", (await semantic.EntityStore<EntityOperationTransportTests.Item>().FindAsync("bulk"))!.Name);
            }
            await using var reopened = await SemanticDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = clock
            });
            var reopenedEngine = (SemanticDatabaseEngine)reopened;
            var reopenedKernel = ((SemanticProfileCommitLog)reopenedEngine.TransferRuntime).PartitionKernel;
            using (var pin = await reopenedKernel.PartitionTransactions.CaptureSnapshotAsync())
            {
                object recovered = await reopenedKernel.PartitionTransactions.CaptureTimestampProjectionAsync(
                    pin, DatabaseTimestampReadClock.Read(clock.Domain, clock), null);
                var field = session.Pages.SelectMany(page => page.Changes).SelectMany(change => change.Fields)
                    .First(change => change.Value is not null);
                using (reopenedKernel.PartitionTransactions.EnterTimestampProjection(recovered))
                {
                    Assert.AreEqual(1, await QueryRetainedAsync(reopenedEngine, new(field.Field, field.Value!)));
                }
            }
            var versionBeforeReplay = reopened.CurrentVersion;
            await reopened.BulkLoadAsync(session);
            Assert.AreEqual(versionBeforeReplay, reopened.CurrentVersion);
            Assert.AreEqual("value", (await reopened.EntityStore<EntityOperationTransportTests.Item>().FindAsync("bulk"))!.Name);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    private static async Task<int> QueryRetainedAsync(SemanticDatabaseEngine engine, SemanticFieldEqualityQuery query)
    {
        // Use the same retained-state entrance as distributed timestamp reads. Ordinary
        // local QueryAsync deliberately selects its own native-version snapshot.
        using var retained = await engine.AcquireDistributedStateAsync(engine.CurrentVersion, default);
        var view = retained.RequireReadView(engine);
        Assert.AreEqual(0, view.State.AppliedSessions.Count, "Timestamp reads must not retain native replay bookkeeping.");
        var result = await view.FieldQueries.ExecuteAsync(view.State, view.State.PropertyStorage,
            query, view.State.Version, default);
        return result.Evidence.Count;
    }
}
