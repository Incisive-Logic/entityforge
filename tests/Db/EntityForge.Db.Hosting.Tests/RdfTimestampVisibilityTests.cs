using EntityForge.Db.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfTimestampVisibilityTests
{
    [TestMethod]
    public async Task Logical_quad_indexes_survive_reverse_publication_retraction_and_reclamation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ef-rdf-timestamp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var pages = await PhysicalPageFile.OpenAsync(Path.Combine(directory, "pages"));
            var runtime = new RdfProfileRuntime(await RdfPhysicalAccessPath.OpenAsync(pages, await PhysicalPageSpace.OpenAsync(pages)));
            var history = (IDatabaseTimestampProfile)runtime;
            Guid domain = Guid.NewGuid();
            DurableTimestampState Time(long value) => new(domain, value, 0, 0, true);
            history.InitializeTimestampHistory(Time(0));
            var subject = new RdfIri("urn:subject");
            var predicate = new RdfIri("urn:predicate");
            var first = new RdfQuad(subject, predicate, new RdfLiteral("first"));
            var later = new RdfQuad(new RdfIri("urn:other"), predicate, new RdfLiteral("later"), new RdfIri("urn:graph"));
            long version = 0;
            async Task Apply(long time, params RdfMutation[] mutations)
            {
                Guid operation = Guid.NewGuid();
                byte[] payload = RdfProfileRuntime.Encode(new(operation, mutations));
                await runtime.ApplyAsync(new(++version), operation, payload);
                await runtime.PublishStagedStateAsync(operation);
                history.PublishTimestampVersion(operation, Time(time), payload);
            }
            await Apply(1, new AssertRdfQuad(first));
            object pinned = history.CaptureTimestampView(Time(1));
            await Apply(3, new AssertRdfQuad(later));
            await Apply(2, new RetractRdfQuad(first));
            RdfSnapshotLease escaped;
            using (history.EnterTimestampView(pinned))
            {
                escaped = runtime.AcquireTimestampProjectionLease(new(1));
                Assert.IsTrue(escaped.IsActive);
                CollectionAssert.AreEqual(new[] { first }, runtime.Find(new(Subject: subject)).ToArray());
                Assert.AreEqual(1, runtime.CountInMemory(predicate, first.Object, null));
                Assert.AreEqual(0, runtime.Find(new(Graph: later.Graph)).Count);
            }
            Assert.IsFalse(escaped.IsActive);
            using (history.EnterTimestampView(pinned))
            {
                Assert.IsFalse(escaped.IsActive, "An escaped lease must not revive when the same immutable view is entered again.");
                using var fresh = runtime.AcquireTimestampProjectionLease(new(1));
                Assert.IsTrue(fresh.IsActive);
            }
            escaped.Dispose();
            using (history.EnterTimestampView(history.CaptureTimestampView(Time(2))))
            {
                Assert.AreEqual(0, runtime.Find(new()).Count);
            }

            using (history.EnterTimestampView(history.CaptureTimestampView(Time(3))))
            {
                CollectionAssert.AreEqual(new[] { later }, runtime.Find(new()).ToArray());
                CollectionAssert.AreEqual(new[] { later }, runtime.FindBounded(new(Graph: later.Graph), new(1), 1, default).ToArray());
            }
            history.TrimTimestampVersions(Time(2));
            Assert.ThrowsExactly<InvalidOperationException>(() => history.CaptureTimestampView(Time(1)));
            using (history.EnterTimestampView(history.CaptureTimestampView(Time(3))))
            {
                Assert.AreEqual(1, runtime.Find(new()).Count);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
