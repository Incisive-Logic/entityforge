using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Sparql_persisted_numeric_sort_spills_and_preserves_snapshot_and_window(bool descending, bool windowed)
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string xsd = "http://www.w3.org/2001/XMLSchema#";
        const int repetitions = 900;
        RdfTerm[] ordered = [new RdfLiteral("-INF", xsd + "double"), new RdfLiteral("0", xsd + "integer"),
            new RdfLiteral("9007199254740992", xsd + "double"), new RdfLiteral("9007199254740993", xsd + "integer"),
            new RdfLiteral("INF", xsd + "float"), new RdfLiteral("NaN", xsd + "double"), new RdfLiteral("1x", xsd + "integer")];
        var quads = Enumerable.Range(0, ordered.Length * repetitions).Select(index =>
            new RdfQuad(new RdfIri("urn:ordered:" + index.ToString("D5")), new("urn:p"), ordered[index % ordered.Length])).ToArray();
        var version = await database.BulkLoadAsync(Guid.NewGuid(), quads.Reverse().ToArray());
        int reads = 0;
        async ValueTask<RdfQueryPage> Read(RdfQuadPattern pattern, int size, string? cursor, CancellationToken cancellation)
        {
            var page = await database.QueryPageAsync(pattern, size, cursor, cancellation);
            if (++reads == 1)
            {
                await database.CommitAsync(new(Guid.NewGuid(), [new RetractRdfQuad(quads[0])]));
            }
            return page;
        }
        int offset = windowed ? 901 : 0, limit = windowed ? 4001 : quads.Length;
        string query = "SELECT ?s ?v WHERE { ?s <urn:p> ?v } ORDER BY " +
            (descending ? "DESC(?v)" : "?v") + " ?s LIMIT " + limit + " OFFSET " + offset;
        int rows = 0, complete = 0;
        await foreach (var page in RdfSparql.ScanAsync(query, Read, database.ReleaseQueryContinuationAsync,
            new(PageSize: 37, MaximumSortTemporaryBytes: 16 * 1024 * 1024)))
        {
            Assert.AreEqual(version.Value, page.SnapshotVersion);
            foreach (var row in page.Rows)
            {
                int position = offset + rows++, group = position / repetitions;
                int rank = descending ? ordered.Length - 1 - group : group;
                int index = rank + ordered.Length * (position % repetitions);
                Assert.AreEqual(ordered[rank], row["v"]);
                Assert.AreEqual(quads[index].Subject, row["s"]);
            }
            complete += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(limit, rows);
        Assert.AreEqual(1, complete);
        Assert.IsTrue(reads > 1);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }
}
