using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Independent_pipeline_keeps_each_root_replay_and_retraction(DatabaseProfileId profile)
    {
        await WithProfileFragments(profile, async (_, _, _, client, http, nodes, _) =>
        {
            PartitionIndependentWrite Capture(string key)
            {
                var operation = EntityOperationIds.New();
                ReadOnlyMemory<byte> bytes = profile switch
                {
                    DatabaseProfileId.PropertyGraph => new HttpGraphEntityClient(http, client.Generation).EntityStore<Item>()
                        .CaptureSave(new(key, "before"), new GraphEntityWriteOptions(operation)).Export(),
                    DatabaseProfileId.Rdf => new HttpRdfEntityClient(http, client.Generation).EntityStore<Item>()
                        .CaptureSave(new(key, "before"), new RdfEntityWriteOptions(operation)).Export(),
                    _ => new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>()
                        .CaptureSave(new(key, "before"), new SemanticEntityWriteOperation(operation, DateTimeOffset.UtcNow)).Export()
                };
                return new(operation, [bytes]);
            }
            async Task<string?> Read(string key) => profile switch
            {
                DatabaseProfileId.PropertyGraph => (await client.ReadGraphAsync<Item>(key)).Value?.Name,
                DatabaseProfileId.Rdf => (await client.ReadRdfAsync<Item>(key)).Value?.Name,
                _ => (await client.ReadSemanticAsync<Item>(key)).Value?.Name
            };
            // Bootstrap mappings, then test genuinely concurrent fresh roots.
            var seed = Capture("seed");
            await client.DeliverAsync(seed.Operation, seed.Requests);
            int roots = profile == DatabaseProfileId.Semantic ? HttpPartitionEntityClient.MaximumIndependentWrites : 16;
            var writes = Enumerable.Range(0, roots).Select(i => Capture(i.ToString())).ToArray();
            var results = await client.DeliverIndependentAsync(writes, HttpPartitionEntityClient.MaximumIndependentWriteConcurrency);
            Assert.IsTrue(results.All(result => result.Failure is null),
                string.Join(Environment.NewLine, results.Select(result => result.Failure?.ToString())));
            CollectionAssert.AreEqual(writes.Select(w => w.Operation).ToArray(), results.Select(r => r.Operation).ToArray());
            foreach (var key in Enumerable.Range(0, roots).Select(i => i.ToString()))
            {
                Assert.AreEqual("before", await Read(key));
            }
            // Replay must not write. Keep unrelated physical maintenance commits
            // outside this exact-version assertion, as checkpoint capture does.
            var priorities = nodes.Select(node => typeof(EntityForge.Db.Kernel.DatabaseKernel)
                .GetField("_profile", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(node.Kernel))
                .OfType<EntityForge.Db.Kernel.IDatabaseProfileCheckpointPriority>().Select(profile => profile.EnterCheckpointPriority()).ToArray();
            try
            {
                var versions = nodes.Select(n => n.Kernel.CurrentVersion).ToArray();
                var replay = await client.DeliverIndependentAsync(writes);
                Assert.IsTrue(replay.All(result => result.Failure is null));
                CollectionAssert.AreEqual(versions, nodes.Select(n => n.Kernel.CurrentVersion).ToArray());
            }
            finally
            {
                foreach (var priority in priorities)
                {
                    priority.Dispose();
                }
            }
            if (profile == DatabaseProfileId.Semantic)
            {
                await client.DeleteSemanticAsync<Item>("0");
            }
            else if (profile == DatabaseProfileId.Rdf)
            {
                await client.DeleteRdfAsync<Item>("0");
            }
            else
            {
                await client.DeleteGraphAsync<Item>("0");
            }

            Assert.IsNull(await Read("0"));
            Assert.AreEqual("before", await Read("1"), "One root's retraction must not remove another root's contribution.");
            Assert.IsTrue(nodes.All(n => n.ActiveQueryJobCount == 0));
        });
    }

    [TestMethod]
    public async Task Independent_pipeline_rejects_duplicates_and_reports_unsent_cancellation()
    {
        using var http = new HttpClient();
        var client = new HttpPartitionEntityClient(http, new Uri("https://unused.invalid"), "unused", Guid.NewGuid(), DatabaseProfileId.Semantic);
        var write = new PartitionIndependentWrite(EntityOperationIds.New(), [new byte[] { 1 }]);
        await Assert.ThrowsAsync<ArgumentException>(async () => await client.DeliverIndependentAsync([write, write]));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var results = await client.DeliverIndependentAsync([write], token: cancelled.Token);
        Assert.IsNull(results[0].Receipt);
        Assert.IsInstanceOfType<OperationCanceledException>(results[0].Failure);
        Assert.AreEqual(write.Operation, results[0].Operation);
    }
}
