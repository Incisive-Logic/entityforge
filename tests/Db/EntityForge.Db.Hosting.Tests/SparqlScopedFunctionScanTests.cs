using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT (BNODE('x') AS ?a) (BNODE('x') AS ?b) (NOW() AS ?t) (NOW() AS ?u) { ?s <urn:p> ?o }")]
    [DataRow("SELECT ?a (BNODE('x') AS ?b) ?t (NOW() AS ?u) { ?s <urn:p> ?o BIND(BNODE('x') AS ?a) BIND(NOW() AS ?t) FILTER(?a=BNODE('x') && ?t=NOW()) }")]
    [DataRow("SELECT ?a ?b ?t ?u { ?s <urn:p> ?o BIND(BNODE('x') AS ?a) BIND(BNODE('x') AS ?b) BIND(NOW() AS ?t) BIND(NOW() AS ?u) } ORDER BY ?s")]
    public async Task Sparql_scoped_functions_preserve_solution_identity_and_clock_across_pages(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.BulkLoadAsync(Guid.NewGuid(), Enumerable.Range(0, 5)
            .Select(index => new RdfQuad(new RdfIri("urn:s:" + index), new("urn:p"), new RdfLiteral("value"))).ToArray());
        var allIdentities = new HashSet<RdfTerm>();
        void Check(IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows)
        {
            Assert.AreEqual(5, rows.Count);
            var time = rows[0]["t"];
            Assert.AreEqual("http://www.w3.org/2001/XMLSchema#dateTime", ((RdfLiteral)time).Datatype);
            foreach (var row in rows)
            {
                Assert.IsInstanceOfType<RdfBlankNode>(row["a"]);
                Assert.AreEqual(row["a"], row["b"]);
                Assert.IsTrue(allIdentities.Add(row["a"]), "Blank nodes must be fresh between solutions and executions.");
                Assert.AreEqual(time, row["t"]);
                Assert.AreEqual(time, row["u"]);
            }
        }
        Check(RdfSparql.Execute(database, query));
        for (int execution = 0; execution < 2; execution++)
        {
            var rows = new List<IReadOnlyDictionary<string, RdfTerm>>();
            int completions = 0;
            await foreach (var page in RdfSparql.ScanAsync(query, database.QueryPageAsync,
                database.ReleaseQueryContinuationAsync, new(PageSize: 2, MaximumSortTemporaryBytes: 65536)))
            {
                rows.AddRange(page.Rows);
                completions += page.Complete ? 1 : 0;
            }
            Check(rows);
            Assert.AreEqual(1, completions);
            Assert.AreEqual(0, database.RetainedQueryContinuationCount);
        }
    }
}
