using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlAtomicCommitTests
{
    private readonly TemporaryTestDirectories _directories = new("entityforge-gql-atomic");

    [TestCleanup]
    public void Cleanup() => _directories.Cleanup();

    [TestMethod]
    public async Task Interleaved_reads_observe_statistics_and_values_after_multiple_write_stages()
    {
        await using var database = await GraphDatabase.OpenLocalAsync(new(_directories.Create()));
        await GraphGqlUpdateExecution.ApplyAsync(database,
            new("INSERT (n:Deferred {amount:1})", Guid.CreateVersion7()), default);
        var applied = await GraphGqlUpdateExecution.ApplyAsync(database, new(
            "MATCH (n:Deferred) SET n.amount=2 RETURN n NEXT MATCH (m:Deferred) WHERE m.amount=2 SET m.amount=3 RETURN m.amount AS amount",
            Guid.CreateVersion7()), default);
        var rows = applied.Result!.Value.GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual(3L, rows[0].GetProperty("bindings").GetProperty("amount").GetInt64());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Native_binding_and_write_replay_exact_result_after_reopen(bool separateBinding)
    {
        string directory = _directories.Create();
        var observer = new FlushObserver();
        var insert = new GraphGqlMutationRequest("INSERT (n:Atomic {amount:1})", Guid.CreateVersion7());
        var update = new GraphGqlMutationRequest("MATCH (n:Atomic) SET n.amount=n.amount+1 RETURN n.amount AS amount", Guid.CreateVersion7());
        DatabaseVersion version;
        string result;
        await using (var database = await GraphDatabase.OpenLocalAsync(new(directory), observer, null))
        {
            Assert.IsTrue(((GraphDatabaseEngine)database).SupportsBoundCommit);
            if (separateBinding)
            {
                ((IEntityOperationHost)database).Operations.AfterBindingPersisted = () => ValueTask.CompletedTask;
            }
            await GraphGqlUpdateExecution.ApplyAsync(database, insert, default);
            int before = observer.Flushes;
            var applied = await GraphGqlUpdateExecution.ApplyAsync(database, update, default);
            Assert.AreEqual(separateBinding ? 2 : 1, observer.Flushes - before, "Atomic binding must use one durable group; observer fallback retains two.");
            version = applied.Version;
            result = applied.Result!.Value.GetRawText();
            var replay = await GraphGqlUpdateExecution.ApplyAsync(database, update, default);
            Assert.AreEqual(version, replay.Version);
            Assert.AreEqual(result, replay.Result!.Value.GetRawText());
            Assert.AreEqual(separateBinding ? 2 : 1, observer.Flushes - before, "Replay must not append a new write.");
            await Assert.ThrowsAsync<EntityForgeDbIdempotencyException>(async () =>
                await GraphGqlUpdateExecution.ApplyAsync(database, update with
                {
                    Statement = "INSERT (n:Changed)"
                }, default));
        }
        await using (var database = await GraphDatabase.OpenLocalAsync(new(directory)))
        {
            var replay = await GraphGqlUpdateExecution.ApplyAsync(database, update, default);
            Assert.AreEqual(version, replay.Version);
            Assert.AreEqual(result, replay.Result!.Value.GetRawText());
        }
    }

    [TestMethod]
    public async Task Separately_persisted_binding_retains_its_plan_when_atomic_path_resumes()
    {
        await using var database = await GraphDatabase.OpenLocalAsync(new(_directories.Create()));
        var operations = ((IEntityOperationHost)database).Operations;
        var request = new GraphGqlMutationRequest("INSERT (n:Retained {amount:7})", Guid.CreateVersion7());
        operations.AfterBindingPersisted = () => throw new IOException("Lost binding acknowledgement.");
        await Assert.ThrowsAsync<IOException>(async () => await GraphGqlUpdateExecution.ApplyAsync(database, request, default));
        operations.AfterBindingPersisted = null;
        var applied = await GraphGqlUpdateExecution.ApplyAsync(database, request, default);
        var replay = await GraphGqlUpdateExecution.ApplyAsync(database, request, default);
        Assert.AreEqual(applied.Version, replay.Version);
        Assert.AreEqual(applied.Result!.Value.GetRawText(), replay.Result!.Value.GetRawText());
    }

    [TestMethod]
    public async Task Retained_query_plan_is_not_rebound_when_its_snapshot_is_no_longer_available()
    {
        await using var database = await GraphDatabase.OpenLocalAsync(new(_directories.Create()));
        var operations = ((IEntityOperationHost)database).Operations;
        var request = new GraphGqlMutationRequest("INSERT (n:Retained {amount:7}) RETURN n.amount AS amount", Guid.CreateVersion7());
        operations.AfterBindingPersisted = () => throw new IOException("Lost binding acknowledgement.");
        await Assert.ThrowsAsync<IOException>(async () => await GraphGqlUpdateExecution.ApplyAsync(database, request, default));
        operations.AfterBindingPersisted = null;
        await Assert.ThrowsAsync<EntityForge.Db.Graph.Contracts.GraphWriteConflictException>(async () =>
            await GraphGqlUpdateExecution.ApplyAsync(database, request, default));
    }

    [TestMethod]
    public async Task Durable_group_failure_recovers_and_replays_without_reexecuting_mutation()
    {
        string directory = _directories.Create();
        var observer = new FlushObserver();
        var request = new GraphGqlMutationRequest("MATCH (n:Fault) SET n.amount=n.amount+1 RETURN n.amount AS amount", Guid.CreateVersion7());
        await using (var database = await GraphDatabase.OpenLocalAsync(new(directory), observer, null))
        {
            await GraphGqlUpdateExecution.ApplyAsync(database, new("INSERT (n:Fault {amount:1})", Guid.CreateVersion7()), default);
            observer.FailOnce = true;
            await Assert.ThrowsAsync<Exception>(async () => await GraphGqlUpdateExecution.ApplyAsync(database, request, default));
        }
        await using (var database = await GraphDatabase.OpenLocalAsync(new(directory)))
        {
            var replay = await GraphGqlUpdateExecution.ApplyAsync(database, request, default);
            var again = await GraphGqlUpdateExecution.ApplyAsync(database, request, default);
            Assert.AreEqual(replay.Version, again.Version);
            Assert.AreEqual(replay.Result!.Value.GetRawText(), again.Result!.Value.GetRawText());
            Assert.AreEqual(2, replay.Result.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("amount").GetInt32());
        }
    }

    [TestMethod]
    public async Task Program_preview_updates_cached_columns_without_changing_the_leased_source_view()
    {
        await using var database = await GraphDatabase.OpenLocalAsync(new(_directories.Create()));
        await GraphGqlUpdateExecution.ApplyAsync(database,
            new("INSERT (n:Preview {key:1,amount:4})", Guid.CreateVersion7()), default);
        using var lease = await database.AcquireSnapshotAsync();
        var captured = ((GraphDatabaseEngine)database).CaptureQuerySnapshot(lease.Version);
        var vertex = captured.View.Vertices.Values.Single();
        for (int i = 0; i < 10; i++)
        {
            Assert.AreEqual(1, captured.View.FindByProperty("key", new DatabaseValue.Int64(1)).Count);
        }
        var request = new GraphGqlMutationRequest(
            "MATCH (n:Preview) WHERE n.key=1 SET n.key=2,n.amount=n.amount+1 RETURN n.key AS key,n.amount AS amount", Guid.CreateVersion7());
        var result = await GraphGqlUpdateExecution.ApplyAsync(database, request, default);
        var row = result.Result!.Value.GetProperty("matches")[0].GetProperty("bindings");
        Assert.AreEqual(2, row.GetProperty("key").GetInt32());
        Assert.AreEqual(5, row.GetProperty("amount").GetInt32());
        Assert.AreEqual(new DatabaseValue.Int64(1), captured.View.GetVertex(vertex.Id)!.Properties["key"]);
        Assert.AreEqual(new DatabaseValue.Int64(4), database.GetVertex(vertex.Id, lease.Version)!.Properties["amount"]);
        Assert.AreEqual(new DatabaseValue.Int64(5), database.GetVertex(vertex.Id)!.Properties["amount"]);
        var current = ((GraphDatabaseEngine)database).CaptureQuerySnapshot(result.Version);
        Assert.AreEqual(0, current.View.FindByProperty("key", new DatabaseValue.Int64(1)).Count);
        Assert.AreEqual(1, current.View.FindByProperty("key", new DatabaseValue.Int64(2)).Count);
    }

    private sealed class FlushObserver : IRedoDurabilityObserver
    {
        internal int Flushes;
        internal bool FailOnce;

        public void Reached(RedoDurabilityStage stage)
        {
            if (stage != RedoDurabilityStage.AfterWalFlush)
            {
                return;
            }
            Interlocked.Increment(ref Flushes);
            if (FailOnce)
            {
                FailOnce = false;
                throw new IOException("Lost acknowledgement after durable WAL group.");
            }
        }
    }
}
