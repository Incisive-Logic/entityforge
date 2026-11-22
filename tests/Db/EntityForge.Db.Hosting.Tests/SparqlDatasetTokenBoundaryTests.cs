using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT ?value FROM<urn:g>WHERE{?s <urn:p> ?value} ORDER BY ?value", "a,shared")]
    [DataRow("SELECT ?value FROM<urn:g>FROM<urn:h>WHERE{?s <urn:p> ?value} ORDER BY ?value", "a,b,shared")]
    [DataRow("SELECT ?value FROM NAMED<urn:g>WHERE{GRAPH <urn:g>{?s <urn:p> ?value}} ORDER BY ?value", "a,shared")]
    [DataRow("PREFIX ex:<urn:> SELECT ?value FROM ex:gWHERE {?s <urn:p> ?value}", "")]
    [DataRow("PREFIX ex:<urn:> SELECT ?value FROM ex:g{?s <urn:p> ?value} ORDER BY ?value", "a,shared")]
    public Task Sparql_dataset_iri_tokens_need_no_trailing_whitespace(string query, string expected) =>
        Sparql11_dataset_selection_matches_independent_reference(query, expected);

    [TestMethod]
    [DataRow("SELECT*FROM<urn:g>WHERE{?s <urn:p> ?value} ORDER BY ?value")]
    [DataRow("SELECT?value FROM<urn:g>WHERE{?s <urn:p> ?value} ORDER BY ?value")]
    [DataRow("SELECT(?v AS ?value)FROM<urn:g>WHERE{?s <urn:p> ?v} ORDER BY ?value")]
    [DataRow("SELECT DISTINCT?value FROM<urn:g>WHERE{?s <urn:p> ?value} ORDER BY ?value")]
    [DataRow("SELECT REDUCED*FROM<urn:g>WHERE{?s <urn:p> ?value} ORDER BY ?value")]
    public async Task Sparql_dataset_select_header_uses_token_boundaries(string query)
    {
        // The reference engine used by the dataset suite rejects several
        // compact SELECT prefixes. Assert the token grammar and results here
        // without adopting that additional whitespace restriction.
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), DatasetSeed);
        var rows = RdfSparql.Execute(database, query);
        CollectionAssert.AreEqual(new[] { "a", "shared" },
            rows.Select(row => ((RdfLiteral)row["value"]).LexicalForm).ToArray());
        Assert.AreEqual(5, database.Find(new()).Count);
    }

    [TestMethod]
    public async Task Sparql_dataset_token_boundaries_apply_to_all_query_forms()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), DatasetSeed);
        Assert.IsTrue(RdfSparql.ExecuteAsk(database, "ASK FROM<urn:g>WHERE{?s <urn:p> 'a'}"));
        Assert.AreEqual(2, RdfSparql.ExecuteConstruct(database,
            "CONSTRUCT{?s <urn:copy> ?o}FROM<urn:g>WHERE{?s <urn:p> ?o}").Count);
        Assert.AreEqual(1, RdfSparql.ExecuteDescribe(database,
            "DESCRIBE ?s FROM<urn:g>VALUES ?s{<urn:a>}").Count);
        Assert.AreEqual(5, database.Find(new()).Count);
    }
}
