using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("PREFIX ex:<urn:a:> PREFIX ex:<urn:b:>")]
    [DataRow("PREFIX ex:<urn:a:> PREFIX ex:<urn:a:>")]
    [DataRow("PREFIX :<urn:a:> PREFIX :<urn:b:>")]
    [DataRow("PREFIX ex:<urn:a:> BASE <https://example.test/> PREFIX ex:<urn:b:>")]
    public async Task Sparql11_query_rejects_redeclared_prefixes(string prolog)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database, prolog + " SELECT * {}"));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.ExecuteAsk(database, prolog + " ASK {}"));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.ExecuteConstruct(database, prolog + " CONSTRUCT {} WHERE {}"));
    }

    [TestMethod]
    public async Task Sparql11_prefix_labels_are_case_sensitive()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database,
            "PREFIX ex:<urn:a:> PREFIX EX:<urn:b:> SELECT (ex:s AS ?a) (EX:s AS ?b) {}").Single();
        Assert.AreEqual(new RdfIri("urn:a:s"), row["a"]);
        Assert.AreEqual(new RdfIri("urn:b:s"), row["b"]);
    }

    [TestMethod]
    [DataRow("SELECT (IRI('../people/alice') AS ?value) {}", "https://example.test/people/alice")]
    [DataRow("SELECT (URI('/root') AS ?value) {}", "https://example.test/root")]
    [DataRow("SELECT (IRI('//other.test/path') AS ?value) {}", "https://other.test/path")]
    [DataRow("SELECT (IRI('urn:fixed') AS ?value) {}", "urn:fixed")]
    [DataRow("SELECT (IRI('') AS ?value) {}", "https://example.test/base/")]
    [DataRow("SELECT (IRI('#part') AS ?value) {}", "https://example.test/base/#part")]
    [DataRow("SELECT (IRI(?text) AS ?value) { VALUES ?text { 'item' } }", "https://example.test/base/item")]
    [DataRow("SELECT ?value { VALUES ?value { <child> } }", "https://example.test/base/child")]
    [DataRow("PREFIX ex: <../terms/> SELECT ?value { VALUES ?value { ex:child } }", "https://example.test/terms/child")]
    [DataRow("PREFIX ex: <> SELECT ?value { VALUES ?value { ex:child } }", "https://example.test/base/child")]
    public async Task Sparql11_base_resolves_references_and_matches_independent_reference(string body, string expected)
    {
        string query = "BASE <https://example.test/base/> " + body;
        using var store = new VDS.RDF.TripleStore();
        var reference = (VDS.RDF.Query.SparqlResultSet)new VDS.RDF.Query.LeviathanQueryProcessor(store)
            .ProcessQuery(new VDS.RDF.Parsing.SparqlQueryParser().ParseFromString(query));
        await using var database = await OpenAsync(CreateDirectory());
        var actual = RdfSparql.Execute(database, query).Single();
        Assert.AreEqual(new RdfIri(expected), actual["value"]);
        Assert.AreEqual(((VDS.RDF.IUriNode)reference.Single()["value"]).Uri.AbsoluteUri, ((RdfIri)actual["value"]).Value);
    }

    [TestMethod]
    [DataRow("SELECT ?value { { SELECT (IRI('child') AS ?value) {} } }", "https://example.test/base/child")]
    [DataRow("BASE <https://example.test/next/> SELECT (IRI('child') AS ?value) {}", "https://example.test/next/child")]
    [DataRow("PREFIX ex: <terms/> BASE <https://example.test/next/> SELECT ?value { VALUES ?value { ex:child } }", "https://example.test/base/terms/child")]
    public async Task Sparql11_base_scope_follows_prolog_order_and_reaches_subqueries(string body, string expected)
    {
        // SPARQL 1.1 grammar permits repeated BASE declarations. dotNetRDF 3.5.2
        // rejects them and loses the base for IRI in subqueries; assert the spec here.
        await using var database = await OpenAsync(CreateDirectory());
        string query = "BASE <https://example.test/base/> " + body;
        for (int execution = 0; execution < 2; execution++)
        {
            Assert.AreEqual(new RdfIri(expected), RdfSparql.Execute(database, query).Single()["value"]);
        }
    }

    [TestMethod]
    public async Task Sparql11_base_is_static_while_now_is_bound_for_each_execution()
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string query = "BASE <https://example.test/> SELECT (IRI('x') AS ?value) (NOW() AS ?a) (NOW() AS ?b) {}";
        var first = RdfSparql.Execute(database, query).Single();
        await Task.Delay(20);
        var second = RdfSparql.Execute(database, query).Single();
        Assert.AreEqual(first["a"], first["b"]);
        Assert.AreEqual(second["a"], second["b"]);
        Assert.AreNotEqual(first["a"], second["a"]);
        Assert.AreEqual(new RdfIri("https://example.test/x"), second["value"]);
    }

    [TestMethod]
    public async Task Sparql11_base_applies_to_patterns_construct_and_typed_literals_without_rewriting_strings()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(new(new RdfIri("https://example.test/s"),
            new RdfIri("https://example.test/p"), new RdfLiteral("<unchanged>")))]));
        Assert.IsTrue(RdfSparql.ExecuteAsk(database, "BASE <https://example.test/> ASK { <s> <p> ?o FILTER(?o='<unchanged>') }"));
        var result = RdfSparql.ExecuteConstruct(database,
            "BASE <https://example.test/> CONSTRUCT { <copy> <p> ?o } WHERE { <s> <p> ?o }");
        Assert.AreEqual(new RdfIri("https://example.test/copy"), result.Single().Subject);
        var row = RdfSparql.Execute(database,
            "BASE <http://www.w3.org/2001/XMLSchema#> SELECT ('42'^^<int> AS ?value) {}").Single();
        Assert.AreEqual(new RdfLiteral("42", "http://www.w3.org/2001/int"), row["value"]);
        // URI resolution uses path semantics: a fragment is replaced, not a namespace prefix.
        var deleted = await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "BASE <https://example.test/> DELETE WHERE { <s> <p> ?o }");
        Assert.AreEqual(1, deleted.MutationCount);
        Assert.IsFalse(RdfSparql.ExecuteAsk(database, "BASE <https://example.test/> ASK { <s> <p> ?o }"));
    }
}
