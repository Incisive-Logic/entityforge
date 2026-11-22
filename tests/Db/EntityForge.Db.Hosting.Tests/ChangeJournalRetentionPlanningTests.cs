using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class ChangeJournalRetentionPlanningTests
{
    [TestMethod]
    public void Age_protection_expires_at_the_boundary_without_mutating_prior_catalog()
    {
        var clock = new Clock();
        var manifest = Catalog(clock.GetUtcNow(), 5);
        var planner = new ChangeJournalRetentionPlanner(new(1, 2, TimeSpan.FromDays(1)), clock);
        Assert.AreSame(manifest, planner.Plan(manifest).Manifest);
        clock.Now += TimeSpan.FromDays(1);
        var result = planner.Plan(manifest);
        Assert.AreEqual(3, result.Removed.Count);
        Assert.AreEqual(2, result.Manifest.Segments.Count);
        Assert.AreEqual(3L, result.Manifest.RetentionFloorVersion);
        Assert.AreEqual(5, manifest.Segments.Count);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Protected_oldest_entry_prevents_skipping_a_retained_prefix(bool published)
    {
        var clock = new Clock();
        var manifest = Catalog(clock.GetUtcNow(), 5);
        if (published)
        {
            manifest = manifest with
            {
                PublishedCursor = "cursor-1"
            };
        }

        var planner = new ChangeJournalRetentionPlanner(new(1, 1, TimeSpan.Zero), clock);
        var result = planner.Plan(manifest, published ? null : "cursor-1");
        Assert.AreSame(manifest, result.Manifest);
        Assert.AreEqual(0, result.Removed.Count);
    }

    [TestMethod]
    public void Removal_stops_at_prospective_frontier_and_preserves_order()
    {
        var clock = new Clock();
        var manifest = Catalog(clock.GetUtcNow(), 5);
        var planner = new ChangeJournalRetentionPlanner(new(1, 0, TimeSpan.Zero), clock);
        var result = planner.Plan(manifest, "cursor-3");
        CollectionAssert.AreEqual(new long[] { 1, 2 }, result.Removed.Select(x => x.Version).ToArray());
        CollectionAssert.AreEqual(new long[] { 3, 4, 5 }, result.Manifest.Segments.Select(x => x.Version).ToArray());
    }

    private static ChangeJournalManifest Catalog(DateTimeOffset created, int count) => new(
        ChangeJournalManifest.CurrentCodecVersion, 1, Guid.NewGuid(), null, null, $"cursor-{count}",
        Enumerable.Range(1, count).Select(n => new ChangeJournalSegmentDescriptor(
            n.ToString(), "segment", "index", n, Guid.NewGuid(), $"cursor-{n}", created, 100,
            "segment-checksum", "index-checksum")).ToArray());

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 9, 17, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
