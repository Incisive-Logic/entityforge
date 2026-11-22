using System.Text.Json;
using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic;
using EntityForge.Db.Durability;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow("pending-reservation")]
    [DataRow("pending-selection")]
    [DataRow("unrelated-commit")]
    [DataRow("same-entity")]
    [DataRow("same-entity-stale")]
    [DataRow("same-entity-other-source")]
    public async Task Semantic_scoped_save_admission(string schedule)
    {
        await WithProfileFragments(DatabaseProfileId.Semantic, async (_, client, _, _, http, nodes, authority) =>
        {
            var routing = nodes[0].Routing;
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
            var leftOwner = PartitionSemanticOwnerPlan.Owner(store.CaptureSave(new("left", "probe"), SemanticEntityWriteOperation.Create()), routing);
            string rightId = Enumerable.Range(0, 1000).Select(i => "right-" + i).First(id =>
                PartitionSemanticOwnerPlan.Owner(store.CaptureSave(new(id, "probe"), SemanticEntityWriteOperation.Create()), routing) == leftOwner);
            PartitionOwnedNode[] Participants(PartitionTransactionPlan plan) => nodes.Where(node => plan.Members.Any(m => m.Domain == node.Domain)).ToArray();
            foreach (string key in new[] { "left", rightId })
            {
                var seed = store.CaptureSave(new(key, "before"), SemanticEntityWriteOperation.Create());
                await client.DeliverAsync(seed.OperationId, seed.Export());
            }
            var left = store.CaptureSave(new("left", "left-after"), SemanticEntityWriteOperation.Create());
            bool sharedEntity = schedule.StartsWith("same-entity", StringComparison.Ordinal);
            var application = SemanticApplicationIdentity.Default;
            var alternate = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>(new SemanticApplicationIdentity(
                new Uri("urn:test:independent-application"), application.Version, application.TargetVocabulary,
                application.GenerationId, application.ProducerId));
            var right = (schedule == "same-entity-other-source" ? alternate : store).CaptureSave(
                new(sharedEntity ? "left" : rightId, "right-after"), SemanticEntityWriteOperation.Create());
            var leftSource = PartitionSemanticCodec.Original(await PartitionSemanticPlanning.OriginalAsync(
                left.ForDatabase(nodes[0].Domain.Incarnation))).Session.Start.Source;
            var rightSource = PartitionSemanticCodec.Original(await PartitionSemanticPlanning.OriginalAsync(
                right.ForDatabase(nodes[0].Domain.Incarnation))).Session.Start.Source;
            if (!sharedEntity)
            {
                Assert.AreNotEqual(leftSource.SourcePartition, rightSource.SourcePartition,
                "The reproduction must not accidentally contend on one source progress stream.");
            }

            async Task<PartitionTransactionPlan> Select(SemanticEntityRequest request)
            {
                var selection = await PartitionSemanticPlanning.SelectAsync(client.Generation, request.OperationId, routing,
                    nodes.Cast<IPartitionTransactionParticipant>().ToArray(), [request.Export()], default);
                await authority.BindApplicationAsync(selection);
                var plan = PartitionSemanticPlanning.Plan(selection, routing, nodes.Select(n => n.Domain).ToArray());
                await authority.BindAsync(plan);
                return plan;
            }
            var a = await Select(left);
            if (schedule == "pending-selection")
            {
                foreach (var node in Participants(a))
                {
                    await node.PrepareNativeAsync(a);
                }
            }

            var b = await Select(right);
            CollectionAssert.AreEqual(a.Members.Select(m => m.ReadVersion).ToArray(), b.Members.Select(m => m.ReadVersion).ToArray());
            Exception? failure = null;
            if (schedule is "pending-reservation" or "pending-selection" or "same-entity" or "same-entity-other-source")
            {
                foreach (var node in Participants(a))
                {
                    await node.PrepareNativeAsync(a);
                }

                var outcome = await new PartitionTransactionCoordinator(authority).ExecuteAsync(b, Participants(b));
                Assert.AreEqual(!sharedEntity, outcome.Decision.Commit);
                failure = outcome.PreparationFailure;
                if (sharedEntity)
                {
                    Assert.IsInstanceOfType<DatabasePartitionConflictException>(failure);
                }

                var completed = await new PartitionTransactionCoordinator(authority).ExecuteAsync(a, Participants(a));
                Assert.IsTrue(completed.Decision.Commit);
            }
            else
            {
                var committed = await new PartitionTransactionCoordinator(authority).ExecuteAsync(a, Participants(a));
                Assert.IsTrue(committed.Decision.Commit);
                Assert.IsTrue(Participants(b).All(n => n.Kernel.CurrentVersion.Value > b.Members.Single(m => m.Domain.Identity == n.Domain.Identity).ReadVersion.Value));
                var completed = await new PartitionTransactionCoordinator(authority).ExecuteAsync(b, Participants(b));
                Assert.AreEqual(!sharedEntity, completed.Decision.Commit, completed.PreparationFailure?.ToString());
                if (sharedEntity)
                {
                    Assert.IsInstanceOfType<DatabasePartitionConflictException>(completed.PreparationFailure);
                }
            }
            Assert.AreEqual(!sharedEntity, (await authority.ReadDecisionAsync(b.Operation))!.Commit);
            foreach (var node in nodes)
            {
                Assert.IsTrue((await node.Kernel.PartitionTransactions.ReadAsync()).All(p => p.State != DurablePartitionTransactionState.Prepared));
            }

            var replay = await new PartitionTransactionCoordinator(authority).ExecuteAsync(a, Participants(a));
            Assert.IsTrue(replay.Decision.Commit);
            foreach (var node in nodes)
            {
                await node.Kernel.CreateCheckpointAsync();
            }

            string? output = Environment.GetEnvironmentVariable("ENTITYFORGE_SEMANTIC_INVESTIGATION_OUTPUT");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, schedule + ".json"), JsonSerializer.Serialize(new
                {
                    Schedule = schedule,
                    RequestedEntities = new[] { "left", sharedEntity ? "left" : rightId },
                    DistinctSourcePartitions = new[] { leftSource.SourcePartition, rightSource.SourcePartition },
                    SelectedVersions = b.Members.Select(m => new { m.Domain.Partition, m.ReadVersion }),
                    NativeVersions = nodes.Select(n => new { n.Domain.Partition, n.Kernel.CurrentVersion }),
                    Failure = failure?.ToString(),
                    PendingPreparations = 0,
                    SecondDecision = sharedEntity ? "durable-abort" : "commit"
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }, commands: true);
    }
}
