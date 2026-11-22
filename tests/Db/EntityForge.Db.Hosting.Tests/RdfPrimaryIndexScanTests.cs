using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("subject", false)]
    [DataRow("subject", true)]
    [DataRow("object", false)]
    [DataRow("object", true)]
    [DataRow("predicate", false)]
    [DataRow("predicate", true)]
    [DataRow("graph", false)]
    [DataRow("graph", true)]
    [DataRow("default", false)]
    [DataRow("default", true)]
    [DataRow("full", false)]
    [DataRow("full", true)]
    public async Task Scan_access_families_preserve_snapshot_and_page_bounds(string family, bool primary)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var quads = Enumerable.Range(0, 60).Select(index => new RdfQuad(
            new RdfIri("urn:s:" + index), new("urn:p:" + index % 3),
            new RdfLiteral("v:" + index % 5), index % 2 == 0 ? null : new RdfIri("urn:g"))).ToList();
        quads.AddRange(Enumerable.Range(0, 10).Select(index => new RdfQuad(
            new RdfIri("urn:shared"), new("urn:p"), new RdfLiteral("v:" + index))));
        await database.CommitAsync(new(Guid.NewGuid(), quads.Select(q => (RdfMutation)new AssertRdfQuad(q)).ToArray()));
        RdfQuadPattern pattern = family switch
        {
            "subject" => new(Subject: new RdfIri("urn:shared")),
            "object" => new(Object: new RdfLiteral("v:1")),
            "predicate" => new(Predicate: new("urn:p:1")),
            "graph" => new(Graph: new RdfIri("urn:g")),
            "default" => new(MatchDefaultGraphOnly: true),
            _ => new()
        };
        var expected = quads.Where(q =>
            (pattern.Subject is null || Equals(pattern.Subject, q.Subject)) &&
            (pattern.Predicate is null || Equals(pattern.Predicate, q.Predicate)) &&
            (pattern.Object is null || Equals(pattern.Object, q.Object)) &&
            (pattern.Graph is null || Equals(pattern.Graph, q.Graph)) &&
            (!pattern.MatchDefaultGraphOnly || q.Graph is null)).ToArray();
        var actual = new List<RdfQuad>();
        using (var session = await database.OpenScanSessionAsync(100, CancellationToken.None))
        {
            bool changed = false, completed = false;
            long? version = null;
            await foreach (var page in session.ReadAsync(pattern, 3, usePrimaryIndexes: primary))
            {
                version ??= page.SnapshotVersion;
                Assert.AreEqual(version.Value, page.SnapshotVersion);
                Assert.IsTrue(page.Quads.Count <= 3);
                actual.AddRange(page.Quads);
                completed |= page.NextContinuation is null;
                if (!changed)
                {
                    changed = true;
                    await database.CommitAsync(new(Guid.NewGuid(), [new RetractRdfQuad(expected[^1])]));
                    await Assert.ThrowsExactlyAsync<RdfSnapshotRetentionException>(async () => await database.CreateCheckpointAsync());
                }
            }
            Assert.IsTrue(completed);
        }
        CollectionAssert.AreEquivalent(expected, actual.ToArray());
        await database.CreateCheckpointAsync();
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    [DataRow("object", 19, false)]
    [DataRow("object", 20, true)]
    [DataRow("predicate", 19, false)]
    [DataRow("predicate", 20, true)]
    [DataRow("graph", 19, false)]
    [DataRow("graph", 20, true)]
    [DataRow("default", 19, false)]
    [DataRow("default", 20, true)]
    public async Task Primary_scan_indexes_charge_all_candidates_including_filtered_rows(string family, int budget, bool succeeds)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), Enumerable.Range(0, 20).Select(index =>
            (RdfMutation)new AssertRdfQuad(new(new RdfIri("urn:s:" + index), new("urn:p"),
                new RdfLiteral("value"), family == "default" ? null : new RdfIri("urn:g")))).ToArray()));
        RdfQuadPattern pattern = family switch
        {
            "object" => new(Object: new RdfLiteral("value"), Predicate: new("urn:absent")),
            "predicate" => new(Predicate: new("urn:p"), Graph: new RdfIri("urn:absent")),
            "graph" => new(Graph: new RdfIri("urn:g")),
            _ => new(MatchDefaultGraphOnly: true)
        };
        using (var session = await database.OpenScanSessionAsync(budget, CancellationToken.None))
        {
            async Task Read()
            {
                int count = 0;
                await foreach (var page in session.ReadAsync(pattern, 3, usePrimaryIndexes: true))
                {
                    count += page.Quads.Count;
                }
                Assert.AreEqual(family is "graph" or "default" ? 20 : 0, count);
            }
            if (succeeds)
            {
                await Read();
            }
            else
            {
                await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(Read);
            }
        }
        await database.CreateCheckpointAsync();
    }
}
