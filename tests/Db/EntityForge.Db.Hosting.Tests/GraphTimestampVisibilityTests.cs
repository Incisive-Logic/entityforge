using System.Diagnostics;
using EntityForge.Values;
using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GraphTimestampVisibilityTests
{
    [TestMethod]
    public async Task Timestamp_mvcc_property_and_label_indexes_follow_logical_view_and_reclamation()
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-mvcc-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            await using var pages = await PhysicalPageFile.OpenAsync(Path.Combine(path, "pages"));
            var runtime = new GraphProfileRuntime(JsonValueAdmissionPolicy.Default, await GraphPhysicalAccessPath.OpenAsync(pages));
            var history = (IDatabaseTimestampProfile)runtime;
            Guid domain = Guid.NewGuid();
            DurableTimestampState Time(long value) => new(domain, value, 0, 0, true);
            history.InitializeTimestampHistory(Time(0));
            var vertex = GraphVertexId.New();
            long version = 0;
            async Task Apply(long time, params GraphMutation[] mutations)
            {
                Guid operation = Guid.NewGuid();
                var payload = GraphTransactionCodec.Encode(new(operation, mutations));
                await runtime.ApplyAsync(new(++version), operation, payload);
                await runtime.PublishStagedStateAsync(operation);
                history.PublishTimestampVersion(operation, Time(time), payload);
            }
            await Apply(1, new CreateVertex(vertex, ["old"]), new SetVertexProperty(vertex, "name", new DatabaseValue.String("before")));
            object original = history.CaptureTimestampView(Time(1));
            await Apply(2, new SetVertexProperty(vertex, "name", new DatabaseValue.String("after")), new RemoveVertexLabel(vertex, "old"), new AddVertexLabel(vertex, "new"));
            using (history.EnterTimestampView(original))
            {
                CollectionAssert.AreEqual(new[] { vertex }, runtime.FindCandidates(new GraphPropertyCandidatePredicate("name", new DatabaseValue.String("before"))).ToArray());
                Assert.AreEqual(0, runtime.FindCandidates(new GraphPropertyCandidatePredicate("name", new DatabaseValue.String("after"))).Count);
                CollectionAssert.AreEqual(new[] { vertex }, runtime.FindCandidates(new GraphLabelCandidatePredicate("old")).ToArray());
                Assert.AreEqual(0, runtime.FindCandidates(new GraphLabelCandidatePredicate("new")).Count);
            }
            var other = GraphVertexId.New();
            await Apply(3, new CreateVertex(other, []));
            await Apply(5, new AddVertexLabel(other, "later"));
            await Apply(4, new AddVertexLabel(other, "earlier"));
            using (history.EnterTimestampView(history.CaptureTimestampView(Time(4))))
            {
                Assert.IsTrue(runtime.GetVertex(other)!.Labels.Contains("earlier"));
                Assert.IsFalse(runtime.GetVertex(other)!.Labels.Contains("later"));
            }
            using (history.EnterTimestampView(history.CaptureTimestampView(Time(5))))
            {
                Assert.AreEqual(2, runtime.GetVertex(other)!.Labels.Count);
            }

            history.TrimTimestampVersions(Time(2));
            Assert.ThrowsExactly<InvalidOperationException>(() => history.CaptureTimestampView(Time(1)));
            await Apply(6, new DeleteVertex(vertex));
            using (history.EnterTimestampView(history.CaptureTimestampView(Time(2))))
            {
                Assert.IsNotNull(runtime.GetVertex(vertex));
            }

            using (history.EnterTimestampView(history.CaptureTimestampView(Time(6))))
            {
                Assert.IsNull(runtime.GetVertex(vertex));
            }
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    [TestMethod]
    public async Task Timestamp_mvcc_post_force_uncertainty_recovers_real_graph_and_rejects_mode_change()
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-mvcc-force-" + Guid.NewGuid().ToString("N"));
        var prior = DatabaseTimestampCostOptions.QualificationOverride.Value;
        var options = new DatabaseTimestampCostOptions(Guid.NewGuid(), () => Stopwatch.GetElapsedTime(0).Ticks,
            () => true, TimeSpan.FromMilliseconds(50))
        {
            Visibility = true
        };
        DatabaseTimestampCostOptions.QualificationOverride.Value = options;
        var vertex = GraphVertexId.New();
        try
        {
            await using (var graph = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal
            }, new FailAfterForce(), null))
            {
                var kernel = ((GraphDatabaseEngine)graph).PartitionKernel;
                Guid operation = Guid.NewGuid();
                try
                {
                    await kernel.CommitAsync(operation, GraphTransactionCodec.Encode(new(operation, [new CreateVertex(vertex, [])])));
                    Assert.Fail("Expected uncertain durability");
                }
                catch (RedoCommitUncertainException)
                {

                }
                catch (EntityForgeDbCommitException failure) when (failure.InnerException is RedoCommitUncertainException)
                {

                }
                Assert.AreEqual(DatabaseKernelState.RecoveryRequired, kernel.State);
            }
            await using (var graph = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal
            }))
            {
                Assert.IsNotNull(await graph.GetVertexAsync(vertex, graph.CurrentVersion));
            }

            DatabaseTimestampCostOptions.QualificationOverride.Value = options with
            {
                Visibility = false
            };
            await Assert.ThrowsExactlyAsync<EntityForgeDbFormatException>(async () =>
            {
                await using var graph = await GraphDatabase.OpenLocalAsync(new(path)
                {
                    VersioningMode = DatabaseVersioningMode.PartitionLocal
                });
            });
        }
        finally
        {
            DatabaseTimestampCostOptions.QualificationOverride.Value = prior;
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    private sealed class FailAfterForce : IRedoDurabilityObserver
    {
        private bool _failed;

        public void Reached(RedoDurabilityStage stage)
        {
            if (stage == RedoDurabilityStage.AfterWalFlush && !_failed)
            {
                _failed = true;
                throw new IOException("Lost durable acknowledgement");
            }
        }
    }
}
