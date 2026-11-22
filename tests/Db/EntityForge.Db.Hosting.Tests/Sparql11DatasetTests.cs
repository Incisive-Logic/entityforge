using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT ?s FROM <urn:g> WHERE { ?s ?p ?o }")]
    [DataRow("ASK FROM <urn:g> { ?s ?p ?o }")]
    [DataRow("CONSTRUCT { ?s ?p ?o } FROM <urn:g> WHERE { ?s ?p ?o }")]
    [DataRow("DESCRIBE <urn:a> FROM <urn:g>")]
    [DataRow("SELECT ?s FROM NAMED <urn:g> WHERE { GRAPH <urn:g> { ?s ?p ?o } }")]
    [DataRow("ASK FROM NAMED <urn:g> { GRAPH <urn:g> { ?s ?p ?o } }")]
    [DataRow("CONSTRUCT { ?s ?p ?o } FROM NAMED <urn:g> WHERE { GRAPH <urn:g> { ?s ?p ?o } }")]
    [DataRow("DESCRIBE <urn:a> FROM NAMED <urn:g>")]
    public async Task Sparql11_selected_dataset_preserves_query_snapshot_version(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), DatasetSeed);
        var version = database.CurrentVersion;
        Assert.AreNotEqual(default(DatabaseVersion), version);
        var result = await database.QuerySparqlAsync(query);
        Assert.AreEqual(version, result.SnapshotVersion);
        Assert.AreEqual(version, database.CurrentVersion);
    }

    private const string DatasetSeed = """
        INSERT DATA {
          <urn:default> <urn:p> "default" .
          GRAPH <urn:g> { <urn:a> <urn:p> "a" . <urn:shared> <urn:p> "shared" }
          GRAPH <urn:h> { <urn:b> <urn:p> "b" . <urn:shared> <urn:p> "shared" }
        }
        """;

    [TestMethod]
    [DataRow("SELECT ?value FROM <urn:g> WHERE { ?s <urn:p> ?value } ORDER BY ?value", "a,shared")]
    [DataRow("SELECT ?value FROM <urn:g> FROM <urn:h> WHERE { ?s <urn:p> ?value } ORDER BY ?value", "a,b,shared")]
    [DataRow("SELECT ?value FROM <urn:g> FROM <urn:g> WHERE { ?s <urn:p> ?value } ORDER BY ?value", "a,shared")]
    [DataRow("SELECT ?value FROM NAMED <urn:g> WHERE { ?s <urn:p> ?value }", "")]
    [DataRow("SELECT ?value FROM <urn:g> WHERE { GRAPH ?g { ?s <urn:p> ?value } }", "")]
    [DataRow("SELECT ?value FROM NAMED <urn:g> WHERE { GRAPH ?g { ?s <urn:p> ?value } } ORDER BY ?value", "a,shared")]
    [DataRow("SELECT ?value FROM NAMED <urn:g> FROM NAMED <urn:g> WHERE { GRAPH ?g { ?s <urn:p> ?value } } ORDER BY ?value", "a,shared")]
    [DataRow("SELECT ?value FROM NAMED <urn:g> WHERE { GRAPH <urn:h> { ?s <urn:p> ?value } }", "")]
    [DataRow("SELECT ?value FROM <urn:g> FROM NAMED <urn:h> WHERE { GRAPH <urn:h> { ?s <urn:p> ?value } } ORDER BY ?value", "b,shared")]
    [DataRow("SELECT (COUNT(*) AS ?value) FROM <urn:g> FROM <urn:h> WHERE { ?s <urn:p> ?v }", "3")]
    [DataRow("SELECT ?value FROM <urn:g> WHERE { { SELECT ?value { ?s <urn:p> ?value } } } ORDER BY ?value", "a,shared")]
    [DataRow("PREFIX ex: <urn:> SELECT ?value FROM ex:g { ?s ex:p ?value } ORDER BY ?value", "a,shared")]
    [DataRow("SELECT (1<3 AS ?value) FROM <urn:g> WHERE {}", "true")]
    [DataRow("SELECT ('FROM <urn:h>' AS ?value) FROM<urn:g> WHERE {}", "FROM <urn:h>")]
    [DataRow("PREFIX g: <urn:g> SELECT ?value FROM g: WHERE { ?s <urn:p> ?value } ORDER BY ?value", "a,shared")]
    public async Task Sparql11_dataset_selection_matches_independent_reference(string query, string expected)
    {
        using var reference = new VDS.RDF.TripleStore();
        new VDS.RDF.Update.LeviathanUpdateProcessor(reference).ProcessCommandSet(
            new VDS.RDF.Parsing.SparqlUpdateParser().ParseFromString(DatasetSeed));
        var wanted = (VDS.RDF.Query.SparqlResultSet)new VDS.RDF.Query.LeviathanQueryProcessor(reference)
            .ProcessQuery(new VDS.RDF.Parsing.SparqlQueryParser().ParseFromString(query.Replace("1<3", "1 < 3", StringComparison.Ordinal)));
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), RdfSparqlUpdate.Parse(DatasetSeed, "dataset-test")));
        var actual = RdfSparql.Execute(database, query);
        Assert.AreEqual(expected, String.Join(',', actual.Select(row => ((RdfLiteral)row["value"]).LexicalForm)));
        CollectionAssert.AreEqual(wanted.Select(row => ((VDS.RDF.ILiteralNode)row["value"]).Value).ToArray(),
            actual.Select(row => ((RdfLiteral)row["value"]).LexicalForm).ToArray());
        Assert.AreEqual(5, database.Find(new()).Count, "A query dataset must not change stored graph membership.");
    }

    [TestMethod]
    public async Task Sparql11_dataset_scopes_all_query_forms_and_preserves_static_base()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), RdfSparqlUpdate.Parse(DatasetSeed, "dataset-test")));
        Assert.IsTrue(RdfSparql.ExecuteAsk(database, "ASK FROM <urn:g> { <urn:a> <urn:p> 'a' }"));
        Assert.IsFalse(RdfSparql.ExecuteAsk(database, "ASK FROM NAMED <urn:g> { ?s ?p ?o }"));
        var constructed = RdfSparql.ExecuteConstruct(database,
            "CONSTRUCT { ?s <urn:copy> ?o } FROM <urn:g> WHERE { ?s <urn:p> ?o }");
        Assert.AreEqual(2, constructed.Count);
        Assert.IsTrue(constructed.All(quad => quad.Graph is null));
        var described = RdfSparql.ExecuteDescribe(database,
            "DESCRIBE ?s FROM <urn:g> WHERE { ?s <urn:p> 'a' }");
        Assert.AreEqual(1, described.Count);
        Assert.AreEqual(new RdfIri("urn:a"), described[0].Subject);
        var iri = RdfSparql.Execute(database,
            "BASE <https://example.test/> SELECT (IRI('child') AS ?value) FROM <urn:g> WHERE {}").Single()["value"];
        Assert.AreEqual(new RdfIri("https://example.test/child"), iri);
    }

    [TestMethod]
    public async Task Sparql11_dataset_merge_standardizes_blank_nodes_apart()
    {
        await using var database = await OpenAsync(CreateDirectory());
        RdfQuad[] source =
        [
            new(new RdfBlankNode("shared"), new RdfIri("urn:p"), new RdfLiteral("x"), new RdfIri("urn:g")),
            new(new RdfBlankNode("shared"), new RdfIri("urn:p"), new RdfLiteral("x"), new RdfIri("urn:h"))
        ];
        await database.CommitAsync(new(Guid.NewGuid(), source.Select(quad => (RdfMutation)new AssertRdfQuad(quad)).ToArray()));
        var rows = RdfSparql.Execute(database, "SELECT ?s FROM <urn:g> FROM <urn:h> WHERE { ?s <urn:p> 'x' }");
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(2, rows.Select(row => row["s"]).Distinct().Count());
        Assert.IsTrue(rows.All(row => row["s"] is RdfBlankNode));
    }

    [TestMethod]
    [DataRow("SELECT FROM <urn:g> ?s WHERE { ?s ?p ?o }")]
    [DataRow("SELECT ?s WHERE FROM <urn:g> { ?s ?p ?o }")]
    [DataRow("SELECT ?s FROM <urn:g> ?p FROM <urn:h> WHERE { ?s ?p ?o }")]
    [DataRow("ASK WHERE FROM <urn:g> { ?s ?p ?o }")]
    public async Task Sparql11_dataset_rejects_misplaced_clauses(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        try
        {
            if (query.StartsWith("ASK", StringComparison.Ordinal))
            {
                RdfSparql.ExecuteAsk(database, query);
            }
            else
            {
                RdfSparql.Execute(database, query);
            }
            Assert.Fail("A misplaced dataset clause must reject.");
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException)
        {
        }
    }
}
