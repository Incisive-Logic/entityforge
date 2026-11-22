using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("{ BIND(1 AS ?x) BIND(2 AS ?y) } UNION { BIND(2 AS ?y) BIND(1 AS ?x) }", "1", "2")]
    [DataRow("{ VALUES (?x ?y) {(1 2)} } UNION { VALUES (?y ?x) {(2 1)} }", "1", "2")]
    [DataRow("{ VALUES (?x ?y) {(1 2) (1 UNDEF)} } UNION { VALUES (?y ?x) {(2 1) (UNDEF 1)} }", "2", "4")]
    [DataRow("{ VALUES (?x ?y) {(1 2)} } UNION { VALUES (?y ?x) {(1 2)} }", "2", "2")]
    public async Task Sparql_distinct_mapping_identity_ignores_binding_insertion_order(
        string pattern, string distinct, string total)
    {
        string directory = CreateDirectory();
        string query = "SELECT (COUNT(DISTINCT *) AS ?distinct) (COUNT(*) AS ?total) WHERE { " + pattern + " }";
        void Verify(IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows)
        {
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(new RdfLiteral(distinct, "http://www.w3.org/2001/XMLSchema#integer"), rows[0]["distinct"]);
            Assert.AreEqual(new RdfLiteral(total, "http://www.w3.org/2001/XMLSchema#integer"), rows[0]["total"]);
        }
        Guid request = Guid.NewGuid();
        RdfSparqlUpdateCommit? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await OpenAsync(directory);
            Verify(((RdfSparqlSelectResult)await database.QuerySparqlAsync(query)).Bindings);
            var committed = await database.CommitSparqlUpdateAsync(request,
                "INSERT { <urn:result> <urn:distinct> ?distinct; <urn:total> ?total } WHERE { { " + query + " } }");
            receipt ??= committed;
            Assert.AreEqual(receipt, committed);
            Verify(((RdfSparqlSelectResult)await database.QuerySparqlAsync(
                "SELECT ?distinct ?total WHERE { <urn:result> <urn:distinct> ?distinct; <urn:total> ?total }")).Bindings);
        }
    }
}
