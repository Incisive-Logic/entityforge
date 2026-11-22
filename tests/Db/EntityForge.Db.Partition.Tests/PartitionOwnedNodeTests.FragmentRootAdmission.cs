using System.Reflection;
using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow("unrelated")]
    [DataRow("before")]
    [DataRow("after")]
    public Task Public_scalar_fragment_query_waits_for_root_index_append(string filter) =>
        CheckPublicRootAppendAsync(filter, false, false);

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public Task Public_root_index_append_preserves_failure_and_expired_ceiling(bool fail, bool expired) =>
        CheckPublicRootAppendAsync("unrelated", fail, expired);

    [TestMethod]
    [DataRow("unrelated")]
    [DataRow("before")]
    public Task Public_scalar_fragment_query_with_public_writer_preserves_admission(string filter) =>
        CheckPublicRootAppendAsync(filter, false, false, publicWriter: true);

    private async Task CheckPublicRootAppendAsync(string filter, bool fail, bool expired, bool publicWriter = false)
    {
        await WithProfileFragments(DatabaseProfileId.Semantic, async (_, candidate, _, publicClient, http, nodes, authority) =>
        {
            var store = new HttpSemanticEntityClient(http, candidate.Generation).EntityStore<Item>();
            var seed = store.CaptureSave(new("one", "before"), SemanticEntityWriteOperation.Create());
            await candidate.DeliverAsync(seed.OperationId, seed.Export());
            var stable = store.CaptureSave(new("two", "unrelated"), SemanticEntityWriteOperation.Create());
            await candidate.DeliverAsync(stable.OperationId, stable.Export());
            var manifest = store.Manifests[0];
            string property = manifest.ProfileBindings.Single(p => p.MemberIdentity == manifest.Members.Single(m => m.ClrName == "Name").Identity).StoredBinding;
            var query = new PortableEntityQueryRequest(manifest.ProfileTypeBinding,
                new(manifest.ProfileTypeBinding, Fields: [new(property, "name")]),
                Filter: new("propertyComparison", property, "equal", new(JsonSerializer.SerializeToElement(filter))),
                First: 8, ConceptMatchMode: "direct");
            var baseline = await publicClient.QuerySemanticAsync(query);
            Assert.AreEqual("semantic-projection-fragments", baseline.Statistics!.Strategy);
            if (filter == "unrelated")
            {
                Assert.IsTrue(Canonical(baseline.Value, true).Contains("unrelated"), "The preserved query must have a nonempty projected value.");
            }

            var node = nodes.Single(n =>
            {
                var profile = (IDatabaseTimestampProfile)typeof(DatabaseKernel).GetField("_profile", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(n.Kernel)!;
                var view = profile.CaptureTimestampView(n.Kernel.TimestampCostState!);
                var state = (EntityForge.Db.Semantic.Core.SemanticDatabaseState)view.GetType().GetProperty("State")!.GetValue(view)!;
                return state.Fields.Any(f => f.Value.Equals(new EntityForge.Semantics.StringSemanticValue("before")));
            });
            var log = typeof(DatabaseKernel).GetField("_commitLog", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel)!;
            var stream = typeof(GroupedRedoDatabaseCommitLog).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(log)!;
            using var pause = new PausedReadAdmissionWal { Armed = true, FailOnRelease = fail };
            stream.GetType().GetField("_observer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stream, pause);
            var write = store.CaptureSave(new Item("one", "after"), SemanticEntityWriteOperation.Create());
            var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
            Guid transactionId = default;
            // Setup can reserve timestamps or run maintenance. Select the exact
            // planned operation, or an owner-local entity append for public Save.
            pause.ShouldPause = () =>
            {
                var operations = PlannedOwnerOperations(node.Kernel);
                if (!publicWriter)
                {
                    return transactionId != Guid.Empty && operations.Contains(transactionId);
                }

                if (!operations.Any(PartitionOwnerOperation.IsOwnerLocal))
                {
                    return false;
                }

                return ((DatabaseTransactionFootprint[]?)typeof(DatabaseKernel).GetField("_appendReadFootprints", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel)) is { Length: > 0 } footprints &&
                    footprints.Any(f => f.WritesAny(new HashSet<string> { SemanticReadResources.Entity(SemanticEntityReadDecoder.SemanticIdentity(store.DescribePartitionRead("one").Item2.Identity)) }));
            };
            PartitionTransactionMember? planned = null;
            if (!publicWriter)
            {
                var selection = await PartitionSemanticPlanning.SelectAsync(node.Domain.Generation, write.OperationId, routing,
                    nodes, [write.Export()], default);
                await authority.BindApplicationAsync(selection);
                planned = await node.PlanApplicationAsync(write.OperationId);
                transactionId = ((ISemanticCommitDecoder)new SemanticCommitCodec()).Decode(default, planned.Payload.Span).TransactionId;
            }

            var writing = Task.Run(async () =>
            {
                if (publicWriter)
                {
                    await publicClient.SaveSemanticAsync(new Item("one", "after"));
                }
                else
                {
                    await node.Authority.ExecuteAsync(async () => await node.Kernel.CommitWithReceiptAsync(transactionId, planned!.Payload));
                }
            });
            Task<PartitionQueryResult>? reading = null;
            try
            {
                await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(0, node.Kernel.UnresolvedTimestampPreparationCount);
                if (expired)
                {
                    await Task.Delay(600);
                }

                reading = publicClient.QuerySemanticAsync(query).AsTask();
                await Task.Delay(75);
                Assert.IsFalse(reading.IsCompleted, "Fresh public root queries wait for durable publication.");
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
                await Assert.ThrowsAsync<Exception>(async () => await reading!.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.IsTrue(reading!.IsCompleted, "The failed append must terminate the waiting read, not merely time out the test.");
                return;
            }
            else
            {
                await writing.WaitAsync(TimeSpan.FromSeconds(5));
            }

            var result = await reading!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("semantic-projection-fragments", result.Statistics!.Strategy);
            var expected = filter == "unrelated" ? baseline : await publicClient.QuerySemanticAsync(query);
            Assert.AreEqual(Canonical(expected.Value, true), Canonical(result.Value, true));
        });
    }
}
