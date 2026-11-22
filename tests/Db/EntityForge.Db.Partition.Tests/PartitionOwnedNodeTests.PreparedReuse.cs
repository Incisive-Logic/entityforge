using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Kernel;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Semantic_prepared_logical_plan_is_reused_at_exact_predecessor()
    {
        int projections = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Name == "entityforge.db.semantic.planning.stage.duration")
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "entityforge.db.stage" && Equals(tag.Value, "transaction_projection"))
                {
                    Interlocked.Increment(ref projections);
                }
            }
        });
        listener.Start();
        {
            await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
            {
                await remote.CreateTransaction().Save(new Item("registered", "value")).CommitAsync();
                projections = 0;
                long physicalHits = nodes.Sum(node => node.Kernel.RetainedPhysicalPreparationHits);
                var transaction = remote.CreateTransaction();
                for (int i = 0; i < 64; i++)
                {
                    transaction.Save(new Item("reuse-" + i, "value"));
                }

                await transaction.CommitAsync();
                Console.WriteLine($"64-entity native logical projections: {projections}");
                Assert.AreEqual(2L, nodes.Sum(node => node.Kernel.RetainedPhysicalPreparationHits) - physicalHits, "Both owners reuse their exact privately staged physical plan.");
                Assert.AreEqual(2, projections, "Each owner plans once before decision; exact predecessor completion reuses it.");
                Assert.AreEqual("value", (await remote.ReadSemanticAsync<Item>("reuse-63")).Value!.Name);
            });
        }
    }

    [TestMethod]
    public void Binary_scope_fingerprints_are_canonical_framed_and_profile_specific()
    {
        var a = new DatabasePartitionReadScope(DatabaseProfileId.PropertyGraph, ["ab", "c", "unicode-😀"]);
        var b = new DatabasePartitionReadScope(DatabaseProfileId.PropertyGraph, ["unicode-😀", "c", "ab", "ab"]);
        Assert.AreEqual(a.Fingerprint, b.Fingerprint);
        Assert.AreNotEqual(a.Fingerprint, new DatabasePartitionReadScope(DatabaseProfileId.Rdf, ["ab", "c", "unicode-😀"]).Fingerprint);
        Assert.AreNotEqual(new DatabasePartitionReadScope(DatabaseProfileId.Rdf, ["ab", "c"]).Fingerprint, new DatabasePartitionReadScope(DatabaseProfileId.Rdf, ["a", "bc"]).Fingerprint);
    }
}
