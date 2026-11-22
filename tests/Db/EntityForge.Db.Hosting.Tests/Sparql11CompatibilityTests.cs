using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public async Task Sparql11_string_functions_count_unicode_scalars_and_preserve_language()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var rows = RdfSparql.Execute(database,
            "SELECT * { BIND(STRLEN('a😀') AS ?length) BIND(UCASE('Hello'@en) AS ?upper) BIND(LCASE('Hello') AS ?lower) }");
        Assert.AreEqual("2", ((RdfLiteral)rows.Single()["length"]).LexicalForm);
        Assert.AreEqual(new RdfLiteral("HELLO", language: "en"), rows.Single()["upper"]);
        Assert.AreEqual(new RdfLiteral("hello"), rows.Single()["lower"]);
        var filtered = RdfSparql.Execute(database,
            "SELECT ?s { VALUES ?s { 'AB' 'longer' } FILTER(STRLEN(LCASE(?s)) = 2) } ORDER BY STRLEN(?s)");
        Assert.AreEqual(new RdfLiteral("AB"), filtered.Single()["s"]);
    }

    [TestMethod]
    public async Task Sparql11_multiple_having_conditions_and_quoted_keywords()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var rows = RdfSparql.Execute(database,
            "SELECT ?n (COUNT(*) AS ?c) { VALUES ?n { 1 1 2 } } GROUP BY ?n HAVING (COUNT(*) > 1) (?n = 1) ORDER BY ?c ?n");
        Assert.AreEqual("1", ((RdfLiteral)rows.Single()["n"]).LexicalForm);
        Assert.AreEqual(1, RdfSparql.Execute(database, "SELECT * {} ORDER BY STR('HAVING(')").Count);
    }

    [TestMethod]
    public async Task Sparql11_value_function_depth_is_bounded()
    {
        await using var database = await OpenAsync(CreateDirectory());
        string expression = String.Concat(Enumerable.Repeat("STR(", 20)) + "'x'" + new string(')', 20);
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() =>
            RdfSparql.Execute(database, "SELECT * { BIND(" + expression + " AS ?x) }"));
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() =>
            RdfSparql.Execute(database, "SELECT * {} ORDER BY " + expression));
    }

    [TestMethod]
    [DataRow("ORDER BY ?a DESC(?b)", "y,x,z")]
    [DataRow("ORDER BY ASC(?a) DESC(?b) OFFSET 1 LIMIT 1", "x")]
    [DataRow("ORDER BY ?a DESC(?b) OFFSET 1", "x,z")]
    [DataRow("ORDER BY ?a DESC(?b) LIMIT 0", "")]
    [DataRow("ORDER BY DESC(STR(?id))", "z,y,x")]
    [DataRow("ORDER BY DESC(?a + ?b) ?id", "z,y,x")]
    [DataRow("ORDER BY (?a * ?b) DESC(?id)", "x,y,z")]
    [DataRow("ORDER BY IF(?a = 1, -?b, ?b)", "y,x,z")]
    public async Task Sparql11_order_conditions_and_slicing(string modifiers, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var rows = RdfSparql.Execute(database,
            "SELECT ?id { VALUES (?id ?a ?b) { ('x' 1 1) ('z' 2 3) ('y' 1 2) } } " + modifiers);
        Assert.AreEqual(expected, String.Join(",", rows.Select(row => ((RdfLiteral)row["id"]).LexicalForm)));
    }

    [TestMethod]
    public async Task Sparql11_numeric_value_ties_use_the_next_order_condition()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var rows = RdfSparql.Execute(database,
            "SELECT ?id { VALUES (?id ?n) { ('z' 1) ('a' 1.0) } } ORDER BY ?n ?id");
        Assert.AreEqual("a,z", String.Join(",", rows.Select(row => ((RdfLiteral)row["id"]).LexicalForm)));
    }

    [TestMethod]
    public async Task Sparql11_bind_functions_preserve_rows_on_expression_error()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var rows = RdfSparql.Execute(database,
            "SELECT * { VALUES ?v { <urn:a> 'Hello'@en UNDEF } BIND(STR(?v) AS ?text) BIND(LANG(?v) AS ?lang) }");
        Assert.AreEqual(3, rows.Count);
        Assert.AreEqual("urn:a", ((RdfLiteral)rows[0]["text"]).LexicalForm);
        Assert.IsFalse(rows[0].ContainsKey("lang"));
        Assert.AreEqual("en", ((RdfLiteral)rows[1]["lang"]).LexicalForm);
        Assert.AreEqual(0, rows[2].Count);
        var datatype = RdfSparql.Execute(database, "SELECT ?type { BIND(DATATYPE('Hello') AS ?type) }");
        Assert.AreEqual(RdfLiteral.StringDatatype, ((RdfIri)datatype.Single()["type"]).Value);
    }

    [TestMethod]
    [DataRow("VALUES ?v { UNDEF } BIND(1 AS ?v)")]
    [DataRow("?s <urn:absent> ?v BIND(1 AS ?v)")]
    [DataRow("OPTIONAL { ?s <urn:absent> ?v } BIND(1 AS ?v)")]
    public async Task Sparql11_bind_rejects_in_scope_targets_even_without_data(string body)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database, "SELECT * { " + body + " }"));
    }

    [TestMethod]
    [DataRow("ORDER BY")]
    [DataRow("ORDER BY DESC ?x")]
    [DataRow("ORDER BY ASC(?x")]
    [DataRow("LIMIT 1 LIMIT 2")]
    [DataRow("OFFSET 1 OFFSET 2")]
    public async Task Sparql11_malformed_modifiers_are_rejected(string tail)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database, "SELECT * {} " + tail));
    }

    [TestMethod]
    public async Task Sparql11_distributed_order_retains_unprojected_keys()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var version = await database.CommitAsync(new(Guid.NewGuid(), [
            new AssertRdfQuad(new(new RdfIri("urn:x"), new RdfIri("urn:a"), new RdfLiteral("same"))),
            new AssertRdfQuad(new(new RdfIri("urn:y"), new RdfIri("urn:a"), new RdfLiteral("same"))),
            new AssertRdfQuad(new(new RdfIri("urn:x"), new RdfIri("urn:b"), new RdfLiteral("a"))),
            new AssertRdfQuad(new(new RdfIri("urn:y"), new RdfIri("urn:b"), new RdfLiteral("z")))
        ]));
        using var snapshot = await database.AcquireSnapshotAsync(version);
        const string query = "SELECT ?s { ?s <urn:a> ?a; <urn:b> ?b } ORDER BY ?a DESC(STR(?b)) LIMIT 1";
        var local = RdfSparql.Execute(database, query);
        var fragments = RdfSparql.CaptureBindingFragments(database, snapshot, query, 100, default);
        var remote = (RdfSparqlSelectResult)RdfSparql.ExecuteBindingFragments(query, fragments, default);
        Assert.AreEqual(new RdfIri("urn:y"), local.Single()["s"]);
        Assert.AreEqual(local.Single()["s"], remote.Bindings.Single()["s"]);
    }
}

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_multiline_mutations_keep_literal_clause_words()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory)
            .UsePropertyGraphProfile().ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (string statement in new[]
        {
            "INSERT\n(n:Person { id: '" + Guid.NewGuid() + "', name: 'Before' })",
            "MATCH\n(n:Person)\nSET\nn.name = 'SET DELETE, after'"
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) WHERE n.name = $name RETURN n",
            parameters = new
            {
                name = "SET DELETE, after"
            }
        });
        Assert.AreEqual(1, (await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
        using var deleted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH\t(n:Person)\nDELETE\tn"
        });
        Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode);
    }
}
