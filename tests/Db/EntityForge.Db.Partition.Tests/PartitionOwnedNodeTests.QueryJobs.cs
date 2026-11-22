using System.Diagnostics;
using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Query_job_first_execution_releases_registry_and_abandon_owns_drainage(bool abandon)
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "first-command"), DatabaseProfileId.PropertyGraph,
            authority.Lease, Routing, authority, Options, timestampClock: ReadClock);
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(30));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int starts = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.OperationName == "partition.query.job" && Interlocked.Increment(ref starts) == 1)
                {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(15)))
                    {
                        throw new TimeoutException("First-command test release timed out.");
                    }
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        var command = new PartitionQueryJobCommand(Guid.NewGuid(), new("gql", "MATCH (n) RETURN n"), Finish: true);
        var first = Task.Run(async () => await node.ReadQueryJobValueAsync(cut, new("query-job", QueryJob: command, ReadTime: TestQueryTime(cut))));
        Task<PartitionQueryJobResult>? cleanup = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // A stalled first execution must not hold the cut's job registry.
            var second = await Task.Run(async () => await node.ReadQueryJobValueAsync(cut,
                new("query-job", QueryJob: command with
                {
                    Id = Guid.NewGuid()
                }, ReadTime: TestQueryTime(cut)))).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(second.Completed);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await node.ReadQueryJobValueAsync(cut,
                new("query-job", QueryJob: command with
                {
                    Selection = new("gql", "MATCH (n:Changed) RETURN n")
                }, ReadTime: TestQueryTime(cut))));
            if (abandon)
            {
                cleanup = node.ReadQueryJobValueAsync(cut, new("query-job", QueryJob: new(command.Id, Finish: true, Abandon: true), ReadTime: TestQueryTime(cut))).AsTask();
                Assert.IsFalse(cleanup.IsCompleted, "Abandon must join the accepted lifetime.");
            }
            release.Set();
            if (abandon)
            {
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);
                Assert.IsTrue((await cleanup!.WaitAsync(TimeSpan.FromSeconds(5))).Completed);
            }
            else
            {
                Assert.IsTrue((await first.WaitAsync(TimeSpan.FromSeconds(5))).Completed);
            }
        }
        finally
        {
            release.Set();
            try
            {
                await first;
            }
            catch
            {

            }
            if (cleanup is not null)
            {
                await cleanup;
            }

            await node.AbandonCutAsync(cut.Id);
        }
        await using var freeze = await node.Kernel.PartitionTransactions.FreezeAsync();
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Timestamp_query_job_keeps_one_view_across_late_frontier_work(bool fragments)
    {
        var authority = new Authority();
        var clock = new IndependentTestClock(Guid.NewGuid(), 0);
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "timestamp-job"), DatabaseProfileId.PropertyGraph,
            authority.Lease, Routing, authority, Options, timestampClock: clock);
        var adapter = await node.CreateGraphExecutionAsync();
        var captured = adapter.CaptureSave(new Item("root", "before"), EntityOperationIds.New());
        await adapter.ExecuteAsync(node.Domain, captured.OperationId, captured.Export());
        var root = new GraphVertexId(Guid.NewGuid());
        var target = new GraphVertexId(Guid.NewGuid());
        var edge = new GraphEdgeId(Guid.NewGuid());
        var operation = EntityOperationIds.New();
        await node.Kernel.CommitWithReceiptAsync(operation, GraphTransactionCodec.Encode(new(operation,
            [new CreateVertex(root, ["Root"]),
                new SetVertexProperty(root, "key", new DatabaseValue.String("root")),
                new CreateVertex(target, ["Target"]),
                new SetVertexProperty(target, "name", new DatabaseValue.String("before")),
                new CreateEdge(edge, root, target, "peer")], node.Kernel.CurrentVersion, [])));
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20));
        var time = PartitionTimestampClock.Read(clock.Domain, clock);
        var selection = new PartitionQueryExchangeRequest("gql", "MATCH (n:Root)-[e:peer]->(t:Target) WHERE n.key = \"root\" RETURN t",
            GraphFrontier: new(0), GraphFragments: fragments, ContinueLocal: fragments);
        var job = Guid.NewGuid();
        try
        {
            var initial = JsonSerializer.Deserialize<PartitionQueryJobResult>(
                await node.ReadCutAsync(cut, new("query-job", QueryJob: new(job, selection), ReadTime: time)), PartitionParticipantWire.Json)!;
            if (fragments)
            {
                var continuation = initial.Value!.Element.GetProperty("relation").GetProperty("continuations")[0];
                Assert.AreEqual(1, continuation.GetProperty("stage").GetInt32());
                Assert.AreEqual(new DatabaseValue.String("before"), continuation.Deserialize<PartitionQueryContinuation>(PartitionQueryJson.Options)!.Relation.Vertices![0].Properties["name"]);
                Assert.AreEqual(root.Value, initial.Value!.Element.GetProperty("relation").GetProperty("graphBindings")[0].GetGuid());
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadCutAsync(cut, new("query-job",
                    QueryJob: new(job, selection with
                    {
                        GraphFragments = false
                    }), ReadTime: time)).AsTask());
            }
            operation = EntityOperationIds.New();
            await node.Kernel.CommitWithReceiptAsync(operation, GraphTransactionCodec.Encode(new(operation,
                [new SetVertexProperty(target, "name", new DatabaseValue.String("after"))], node.Kernel.CurrentVersion, [])));
            var final = JsonSerializer.Deserialize<PartitionQueryJobResult>(await node.ReadCutAsync(cut, new("query-job",
                QueryJob: new(job, selection with
                {
                    GraphFrontier = new(1, [target.Value], [edge.Value])
                }, Finish: true), ReadTime: time)),
                PartitionParticipantWire.Json)!;
            Assert.IsTrue(final.Completed);
            var vertex = GraphWireJson.Vertex(final.Value!.Element.GetProperty("relation").GetProperty("vertices")[0], target);
            Assert.AreEqual(new DatabaseValue.String("before"), vertex.Properties["name"]);
        }
        finally
        {
            await node.AbandonCutAsync(cut.Id);
        }
        Assert.AreEqual(0, node.Kernel.TimestampProtectionCount);
    }

    [TestMethod]
    public async Task Lost_query_page_reply_is_drained_when_its_parent_cut_is_abandoned()
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "lost-query-page"), DatabaseProfileId.PropertyGraph,
            authority.Lease, Routing, authority, Options, timestampClock: ReadClock);
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20));
        // Discard the response: the caller retains only its attempted cut/job IDs.
        _ = await node.ReadCutAsync(cut, new("query-job", QueryJob: new(Guid.NewGuid(), new("gql", "MATCH (n) RETURN n")), ReadTime: TestQueryTime(cut)));
        await node.AbandonCutAsync(cut.Id).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await using var freeze = await node.Kernel.PartitionTransactions.FreezeAsync();
    }

    [TestMethod]
    public async Task Query_job_reuses_pages_rejects_changed_selection_and_joins_completion()
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node"), DatabaseProfileId.PropertyGraph,
            authority.Lease, Routing, authority, Options, timestampClock: ReadClock);
        var ids = Enumerable.Range(0, 600).Select(_ => new GraphVertexId(Guid.NewGuid())).ToArray();
        var operation = EntityOperationIds.New();
        await node.Kernel.CommitWithReceiptAsync(operation, GraphTransactionCodec.Encode(new(operation,
            ids.Select(id => (GraphMutation)new CreateVertex(id, ["Person"])).ToArray(), new(0), [])));
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20));
        int captures = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
{
    if (a.OperationName == "partition.query.relation.capture")
    {
        Interlocked.Increment(ref captures);
    }

}
        };
        ActivitySource.AddActivityListener(listener);
        var selection = new PartitionQueryExchangeRequest("gql", "MATCH (n:Person) RETURN n LIMIT 1000");
        Guid id = Guid.NewGuid();
        async Task<PartitionQueryJobResult> Read(PartitionQueryJobCommand command) =>
            JsonSerializer.Deserialize<PartitionQueryJobResult>(await node.ReadCutAsync(cut, new("query-job", QueryJob: command, ReadTime: TestQueryTime(cut))), PartitionParticipantWire.Json)!;
        try
        {
            var page = await Read(new(id, selection));
            Assert.IsTrue(page.Valid);
            Assert.IsFalse(page.Completed);
            int count = page.Value!.Element.GetProperty("relation").GetProperty("vertices").GetArrayLength();
            int offset = page.Value.Element.GetProperty("nextOffset").GetInt32();
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Read(new(id, selection with { Statement = "MATCH (n) RETURN n" })));
            // Publishing after the first page must not change the borrowed snapshot
            // or force reconstruction of its already materialised relation.
            operation = EntityOperationIds.New();
            await node.Kernel.CommitWithReceiptAsync(operation, GraphTransactionCodec.Encode(new(operation,
                [new CreateVertex(new(Guid.NewGuid()), ["Person"])], node.Kernel.CurrentVersion, [])));
            do
            {
                page = await Read(new(id, selection with
                {
                    Offset = offset
                }, Finish: true));
                count += page.Value!.Element.GetProperty("relation").GetProperty("vertices").GetArrayLength();
                if (page.Completed)
                {
                    break;
                }

                offset = page.Value.Element.GetProperty("nextOffset").GetInt32();
            } while (true);
            Assert.AreEqual(600, count);
            Assert.AreEqual(1, captures, "Paging must build this relation once.");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                Read(new(id, selection with
                {
                    Statement = "MATCH (n) RETURN n"
                }, Finish: true)));
            const int admissionsExceedingRegistryCapacity = 140;
            for (int index = 0; index < admissionsExceedingRegistryCapacity; index++)
            {
                var empty = await Read(new(Guid.NewGuid(), selection with
                {
                    Statement = "MATCH (n:Absent) RETURN n"
                }, Finish: true));
                Assert.IsTrue(empty.Completed, "Drained jobs must not exhaust admission capacity.");
            }
            var rejected = await Read(new(Guid.NewGuid(), selection, Finish: true));
            Assert.IsTrue(rejected.Valid);
            Assert.IsNotNull(rejected.Value, "A new job at the same timestamp keeps the historical relation.");
        }
        finally
        {
            await node.AbandonCutAsync(cut.Id);
        }
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true, false)]
    [DataRow(DatabaseProfileId.Rdf, false, false)]
    [DataRow(DatabaseProfileId.Rdf, true, false)]
    [DataRow(DatabaseProfileId.Semantic, false, false)]
    [DataRow(DatabaseProfileId.Semantic, true, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, false, true)]
    [DataRow(DatabaseProfileId.PropertyGraph, true, true)]
    [DataRow(DatabaseProfileId.Rdf, false, true)]
    [DataRow(DatabaseProfileId.Rdf, true, true)]
    [DataRow(DatabaseProfileId.Semantic, false, true)]
    [DataRow(DatabaseProfileId.Semantic, true, true)]
    public async Task Query_job_defers_observation_but_never_completes_after_revocation(DatabaseProfileId profile, bool revokeBeforeStart, bool timestamp)
    {
        var authority = new Authority();
        var clock = ReadClock;
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "deferred"), profile,
            authority.Lease, Routing, authority, Options, timestampClock: clock);
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20));
        var time = clock is null ? null : PartitionTimestampClock.Read(clock.Domain, clock);
        var selection = profile == DatabaseProfileId.PropertyGraph
            ? new PartitionQueryExchangeRequest("gql", "MATCH (n) RETURN n")
            : profile == DatabaseProfileId.Rdf ? new PartitionQueryExchangeRequest("sparql", "SELECT ?s ?p ?o WHERE { ?s ?p ?o }")
            : new PartitionQueryExchangeRequest("entity");
        Guid job = Guid.NewGuid();
        int before = Volatile.Read(ref authority.Validations);
        void Revoke()
        {
            var claim = authority.Lease.Placement.Claim;
            authority.Lease = authority.Lease with
            {
                Placement = authority.Lease.Placement with
                {
                    Claim = claim with
                    {
                        FencingToken = new(claim.FencingToken.Value + 1)
                    }
                }
            };
        }
        try
        {
            if (revokeBeforeStart)
            {
                Revoke();
            }

            var provisional = JsonSerializer.Deserialize<PartitionQueryJobResult>(await node.ReadCutAsync(cut,
                new("query-job", QueryJob: new(job, selection), ReadTime: time)), PartitionParticipantWire.Json)!;
            Assert.IsFalse(provisional.Completed);
            Assert.AreEqual(before, Volatile.Read(ref authority.Validations), "Read admission must not observe placement.");
            if (!revokeBeforeStart)
            {
                Revoke();
            }

            await Assert.ThrowsExactlyAsync<StaleShardPlacementException>(() => node.ReadCutAsync(cut,
                new("query-job", QueryJob: new(job, Finish: true), ReadTime: time)).AsTask());
            Assert.AreEqual(before + 1, Volatile.Read(ref authority.Validations), "Completion must freshly observe placement.");
        }
        finally
        {
            await node.AbandonCutAsync(cut.Id);
        }
    }

    [TestMethod]
    public async Task Query_job_abandon_drains_and_revoked_completion_never_returns_success()
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node"), DatabaseProfileId.PropertyGraph,
            authority.Lease, Routing, authority, Options, timestampClock: ReadClock);
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20));
        var selection = new PartitionQueryExchangeRequest("gql", "MATCH (n) RETURN n");
        Guid abandoned = Guid.NewGuid(), revoked = Guid.NewGuid();
        try
        {
            _ = await node.ReadCutAsync(cut, new("query-job", QueryJob: new(abandoned, selection), ReadTime: TestQueryTime(cut)));
            _ = await node.ReadCutAsync(cut, new("query-job", QueryJob: new(abandoned, Finish: true, Abandon: true), ReadTime: TestQueryTime(cut)));
            _ = await node.ReadCutAsync(cut, new("query-job", QueryJob: new(revoked, selection), ReadTime: TestQueryTime(cut)));
            var claim = authority.Lease.Placement.Claim;
            authority.Lease = authority.Lease with
            {
                Placement = authority.Lease.Placement with
                {
                    Claim = claim with
                    {
                        FencingToken = new(claim.FencingToken.Value + 1)
                    }
                }
            };
            await Assert.ThrowsAsync<Exception>(() => node.ReadCutAsync(cut,
                new("query-job", QueryJob: new(revoked, Finish: true), ReadTime: TestQueryTime(cut))).AsTask());
        }
        finally
        {
            await node.AbandonCutAsync(cut.Id);
        }
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    [DataRow(DatabaseProfileId.Semantic, false)]
    [DataRow(DatabaseProfileId.Semantic, true)]
    public async Task Complete_owner_query_runs_native_operators_for_each_profile(DatabaseProfileId profile, bool timestamp)
    {
        var authority = new Authority();
        var clock = ReadClock;
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node"), profile,
            authority.Lease, Routing, authority, Options, transactionAuthority: decisions, timestampClock: clock);
        PartitionQueryRequest query;
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            var adapter = await node.CreateGraphExecutionAsync();
            var capture = adapter.CaptureSave(new Item("one", "value"), EntityOperationIds.New());
            await adapter.ExecuteAsync(node.Domain, capture.OperationId, capture.Export());
            query = new(node.Domain.Generation, profile, "gql", "MATCH (n) RETURN COUNT(n) AS total");
        }
        else if (profile == DatabaseProfileId.Rdf)
        {
            var adapter = await node.CreateRdfExecutionAsync();
            var capture = adapter.CaptureSave(new Item("one", "value"), EntityOperationIds.New());
            await adapter.ExecuteAsync(node.Domain, capture.OperationId, capture.Export());
            query = new(node.Domain.Generation, profile, "sparql", "SELECT ?s ?p ?o WHERE { ?s ?p ?o }");
        }
        else
        {
            var adapter = await node.CreateSemanticExecutionAsync();
            var capture = adapter.CaptureSave(new Item("one", "value"), SemanticEntityWriteOperation.Create());
            await adapter.ExecuteAsync(node.Domain, capture.OperationId, capture.Export());
            using var http = new HttpClient();
            var manifest = new HttpSemanticEntityClient(http, node.Domain.Generation).EntityStore<Item>().Manifests[0];
            query = new(node.Domain.Generation, profile, "entity", EntityQuery: new(manifest.ProfileTypeBinding,
                new(manifest.ProfileTypeBinding), ConceptMatchMode: "direct"));
        }
        var client = new PartitionApplicationClient(decisions, Routing, [node], clock);
        try
        {
            var answer = await client.QueryAsync(query, default);
            Assert.AreEqual("complete-owner-query", answer.Statistics!.Strategy);
            Assert.AreEqual(1, answer.Statistics.ExchangePages);
            Assert.IsFalse(answer.Value.TryGetProperty("snapshotVersion", out _));
            if (profile == DatabaseProfileId.PropertyGraph)
            {
                Assert.AreEqual(1L, answer.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt64());
            }
            else if (profile == DatabaseProfileId.Rdf)
            {
                Assert.IsTrue(answer.Value.GetProperty("bindings").EnumerateArray().Any(row =>
                    row.GetProperty("o").TryGetProperty("lexicalForm", out var text) && text.GetString() == "value"));
            }
            else
            {
                Assert.AreEqual(1, answer.Value.GetProperty("entities").GetArrayLength());
            }
        }
        finally
        {
            await client.CloseReadEpochAsync();
        }
    }
}
