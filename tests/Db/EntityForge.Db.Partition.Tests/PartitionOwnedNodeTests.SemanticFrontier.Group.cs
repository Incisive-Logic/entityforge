using System.Reflection;
using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public Task Semantic_mixed_binding_native_group_rejects_before_staging(bool metadataFirst) =>
        CheckScalarGroupPublicationAsync("unrelated", false, true, true, mixedMetadataFirst: metadataFirst);

    [TestMethod]
    [DataRow("unrelated", false)]
    [DataRow("first-change", false)]
    [DataRow("second-change", false)]
    [DataRow("unrelated", true)]
    public Task Semantic_frontier_waits_for_physical_group_publication(string filter, bool fail) =>
        CheckScalarGroupPublicationAsync(filter, fail, false);

    [TestMethod]
    [DataRow("unrelated", false)]
    [DataRow("first-change", false)]
    [DataRow("second-change", false)]
    [DataRow("unrelated", true)]
    public Task Semantic_fragment_waits_for_physical_group_publication(string filter, bool fail) =>
        CheckScalarGroupPublicationAsync(filter, fail, true);

    [TestMethod]
    [DataRow("unrelated", false)]
    [DataRow("first-change", false)]
    [DataRow("second-change", false)]
    [DataRow("unrelated", true)]
    public Task Semantic_root_index_waits_for_physical_group_publication(string filter, bool fail) =>
        CheckScalarGroupPublicationAsync(filter, fail, true, true);

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public Task Semantic_root_index_reads_cancel_without_acknowledging_writer(bool unrelated) =>
        CheckScalarGroupPublicationAsync(unrelated ? "unrelated" : "second-change", false, true, true, true);

    private async Task CheckScalarGroupPublicationAsync(string filter, bool fail, bool fragment, bool rootRead = false, bool cancelRead = false, bool? mixedMetadataFirst = null)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a")])).Binding;
        var lease = new Authority();
        await using var authority = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "intent-authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "semantic-intent"), DatabaseProfileId.Semantic,
            lease.Lease, routing, lease, Options, transactionAuthority: authority, timestampClock: ReadClock);
        var adapter = await node.CreateSemanticExecutionAsync();
        foreach (string key in new[] { "one", "two" })
        {
            var seed = adapter.CaptureSave(new Item(key, "before"), SemanticEntityWriteOperation.Create());
            await adapter.ExecuteAsync(node.Domain, seed.OperationId, seed.Export());
        }
        using var http = new HttpClient();
        var store = new HttpSemanticEntityClient(http, node.Domain.Generation).EntityStore<Item>();
        async Task<PartitionTransactionMember> Plan(string key)
        {
            var request = store.CaptureSave(new(key, key == "one" ? "first-change" : "second-change"), SemanticEntityWriteOperation.Create());
            var selection = await PartitionSemanticPlanning.SelectAsync(node.Domain.Generation, request.OperationId, routing,
                [node], [request.Export()], default);
            await authority.BindApplicationAsync(selection);
            return await node.PlanApplicationAsync(request.OperationId);
        }
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20));
        using var rootProtection = await node.Kernel.PartitionTransactions.ProtectTimestampHistoryAsync();
        var first = await Plan("one");
        var second = await Plan("two");
        var codec = (ISemanticCommitDecoder)new SemanticCommitCodec();
        var firstId = codec.Decode(default, first.Payload.Span).TransactionId;
        var secondId = codec.Decode(default, second.Payload.Span).TransactionId;
        if (mixedMetadataFirst is bool metadataFirst)
        {
            var nativeHost = typeof(PartitionOwnedNode).GetField("_native", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node)!;
            var engine = (SemanticDatabaseEngine)nativeHost.GetType().GetProperty("Database", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(nativeHost)!;
            var request = store.CaptureSave(new("one", "metadata-plan"), SemanticEntityWriteOperation.Create()).ForDatabase(node.Domain.Incarnation);
            var binding = new EntityForge.Db.Mapping.EntityOperationBinding(request.OperationId,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(request.CanonicalIdentity())), request.PrepareCore(engine).Export());
            var metadataCommit = (EntityForge.Db.Semantic.Durability.DurableDatabaseCommit)typeof(EntityForge.Db.Semantic.Durability.DurableDatabaseCommit)
                .GetMethod("ForApplicationOperation", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [binding])!;
            var metadataPayload = new SemanticCommitCodec().Encode(metadataCommit);
            DatabaseCommitRequest metadata = new(Guid.NewGuid(), metadataPayload);
            DatabaseCommitRequest data = new(firstId, first.Payload);
            DatabaseCommitRequest[] mixed = metadataFirst ? [metadata, data] : [data, metadata];
            var error = await Assert.ThrowsAsync<Exception>(async () => await node.Authority.ExecuteAsync(async () => await node.Kernel.CommitGroupWithReceiptsAsync(mixed)));
            bool excluded = false;
            for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            {
                excluded |= cause is NotSupportedException && cause.Message.Contains("Mixed binding/native");
            }

            Assert.IsTrue(excluded, error.ToString());
            Assert.AreEqual(DatabaseKernelState.Ready, node.Kernel.State);
            Assert.IsNull(OwnerPlanningBasis(engine, "_stagedPlanningEvidence"));
            Assert.IsNull(await ((EntityForge.Db.Mapping.IEntityOperationHost)engine).Operations.FindAsync(request.OperationId, default));
            // Continue with the valid native group below: rejected metadata must leave no
            // private preparation state that can contaminate its planning or publication.
        }
        var manifest = store.Manifests[0];
        string property = manifest.ProfileBindings.Single(p => p.MemberIdentity == manifest.Members.Single(m => m.ClrName == "Name").Identity).StoredBinding;
        var query = new PortableEntityQueryRequest(manifest.ProfileTypeBinding, new(manifest.ProfileTypeBinding, Fields: [new(property, "name")]),
            Filter: new("propertyComparison", property, "equal", new(JsonSerializer.SerializeToElement(filter))), First: fragment ? null : 8, ConceptMatchMode: "direct");

        var log = typeof(DatabaseKernel).GetField("_commitLog", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel)!;
        var stream = typeof(GroupedRedoDatabaseCommitLog).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(log)!;
        using var pause = new PausedReadAdmissionWal { Armed = true, FailOnRelease = fail };
        stream.GetType().GetField("_observer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stream, pause);
        var writing = Task.Run(async () => await node.Authority.ExecuteAsync(async () => await node.Kernel.CommitGroupWithReceiptsAsync([new(firstId, first.Payload), new(secondId, second.Payload)])));
        EntityForge.Db.Durability.DurableTimestampState? secondTime = null;
        Task<byte[]>? reading = null;
        Task<DatabaseRootIndexSnapshot>? rootReading = null;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var native = typeof(PartitionOwnedNode).GetField("_native", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node)!;
            var database = (SemanticDatabaseEngine)native.GetType().GetProperty("Database", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(native)!;
            var stagedEvidence = (SemanticPlanningEvidence?)OwnerPlanningBasis(database, "_stagedPlanningEvidence");
            var publishedEvidence = (SemanticPlanningEvidence?)OwnerPlanningBasis(database, "_planningEvidence");
            Assert.IsNotNull(stagedEvidence);
            Assert.IsNotNull(publishedEvidence);
            Assert.AreNotSame(publishedEvidence, stagedEvidence);
            Assert.IsTrue(stagedEvidence.Version.Value > publishedEvidence.Version.Value);
            var stagedValues = stagedEvidence.Fields.Values.SelectMany(bucket => bucket.Values).Select(field => field.Value.ToString()).ToArray();
            var publishedValues = publishedEvidence.Fields.Values.SelectMany(bucket => bucket.Values).Select(field => field.Value.ToString()).ToArray();
            Assert.IsTrue(stagedValues.Any(value => value.Contains("first-change")));
            Assert.IsTrue(stagedValues.Any(value => value.Contains("second-change")));
            Assert.IsFalse(publishedValues.Any(value => value.Contains("first-change") || value.Contains("second-change")));
            secondTime = node.Kernel.TimestampCostState!;
            var selected = PlannedOwnerOperations(node.Kernel);
            CollectionAssert.AreEquivalent(new[] { firstId, secondId }, selected);
            Assert.AreEqual(0, node.Kernel.UnresolvedTimestampPreparationCount);
            if (rootRead)
            {
                if (cancelRead)
                {
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                        await node.Kernel.PartitionTransactions.CaptureRootIndexesAsync(rootProtection, secondTime, [new("semantic-concept-v1", "")], cancellation.Token));
                }
                rootReading = node.Kernel.PartitionTransactions.CaptureRootIndexesAsync(rootProtection, secondTime, [new("semantic-concept-v1", "")]).AsTask();
            }
            else
            {
                reading = node.ReadCutAsync(cut, new("query-job", ReadTime: secondTime,
                QueryJob: new(Guid.NewGuid(), new("entity", EntityQuery: query, ProfileFragments: fragment, SemanticFrontier: fragment ? null : new(0)), Finish: true))).AsTask();
            }

            await Task.Delay(75);
            Assert.IsFalse(rootRead ? rootReading!.IsCompleted : reading!.IsCompleted, "Fresh reads wait for the whole physical group to publish.");
            Assert.IsFalse(writing.IsCompleted);
        }
        finally
        {
            pause.Release.Set();
        }
        if (fail)
        {
            await Assert.ThrowsAsync<Exception>(async () => await writing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(DatabaseKernelState.RecoveryRequired, node.Kernel.State);
            if (rootRead)
            {
                await Assert.ThrowsAsync<Exception>(async () => await rootReading!.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else
            {
                await Assert.ThrowsAsync<Exception>(async () => await reading!.WaitAsync(TimeSpan.FromSeconds(5)));
            }

            Assert.IsTrue(rootRead ? rootReading!.IsCompleted : reading!.IsCompleted, "Failed publication must terminate the waiting read.");
            await node.AbandonCutAsync(cut.Id);
            return;
        }
        else
        {
            await writing.WaitAsync(TimeSpan.FromSeconds(5));
        }

        if (rootRead)
        {
            var rootResult = await rootReading!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(rootResult.View);
            Assert.AreEqual(1, rootResult.Memberships.Length);
        }
        else
        {
            var result = JsonSerializer.Deserialize<PartitionQueryJobResult>(await reading!.WaitAsync(TimeSpan.FromSeconds(5)), PartitionParticipantWire.Json)!;
            Assert.IsTrue(result.Completed);
            if (filter != "unrelated")
            {
                StringAssert.Contains(result.Value!.Element.GetRawText(), filter);
            }
        }
        Assert.AreEqual(0, node.ActiveQueryJobCount);
        await node.AbandonCutAsync(cut.Id);
    }
}
