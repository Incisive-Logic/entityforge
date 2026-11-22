using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("MIN", false, "1")]
    [DataRow("MIN", true, "1")]
    [DataRow("MAX", false, "2")]
    [DataRow("MAX", true, "2")]
    public async Task Sparql_extrema_argument_error_policy_agrees_between_query_and_scan(
        string function, bool distinct, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string xsd = "http://www.w3.org/2001/XMLSchema#";
        await database.BulkLoadAsync(Guid.NewGuid(), new[] { 0, 1, 2, 0 }.Select((value, index) =>
            new RdfQuad(new RdfIri("urn:input:" + index), new("urn:value"),
                new RdfLiteral(value.ToString(System.Globalization.CultureInfo.InvariantCulture), xsd + "integer"))).ToArray());
        string prefix = function + "(" + (distinct ? "DISTINCT " : "");
        string query = "SELECT (" + prefix + "IF(?v=0,1/0,?v)) AS ?mixed) (" + prefix +
            "1/0) AS ?invalid) WHERE { ?s <urn:value> ?v }";
        void Verify(IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows)
        {
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(new RdfLiteral(expected, xsd + "integer"), rows[0]["mixed"]);
            Assert.IsFalse(rows[0].ContainsKey("invalid"));
        }
        Verify(((RdfSparqlSelectResult)await database.QuerySparqlAsync(query)).Bindings);
        var scanned = new List<IReadOnlyDictionary<string, RdfTerm>>();
        int completions = 0;
        await foreach (var page in RdfSparql.ScanAsync(query, database.QueryPageAsync,
            database.ReleaseQueryContinuationAsync, new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            scanned.AddRange(page.Rows);
            completions += page.Complete ? 1 : 0;
        }
        Verify(scanned);
        Assert.AreEqual(1, completions);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    [DataRow("SUM", "1/?v", false)]
    [DataRow("SUM", "1/?v", true)]
    [DataRow("AVG", "1/?v", false)]
    [DataRow("AVG", "1/?v", true)]
    [DataRow("GROUP_CONCAT", "STR(1/?v)", false)]
    [DataRow("GROUP_CONCAT", "STR(1/?v)", true)]
    public async Task Sparql_grouped_aggregate_errors_preserve_valid_writes_and_replay_after_restart(
        string function, string expression, bool distinct)
    {
        string directory = CreateDirectory();
        Guid transactionId = Guid.NewGuid();
        string statement = "INSERT { ?target <urn:result> ?result ; <urn:group> ?target . [] <urn:marker> ?target } WHERE { { " +
            "SELECT ?target (" + function + "(" + (distinct ? "DISTINCT " : "") + expression + ") AS ?result) " +
            "WHERE { VALUES (?target ?v) { (<urn:a> 1) (<urn:b> 0) (<urn:b> 1) (<urn:c> 1) } } GROUP BY ?target } }";
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await OpenAsync(directory);
            await database.CommitSparqlUpdateAsync(transactionId, statement);
            var quads = database.Find(new()).ToArray();
            Assert.AreEqual(8, quads.Length, "Replay must not allocate new blank-node markers.");
            var markers = quads.Where(quad => quad.Predicate == new RdfIri("urn:marker")).ToArray();
            Assert.AreEqual(3, markers.Length);
            Assert.AreEqual(3, markers.Select(quad => quad.Subject).Distinct().Count());
            Assert.IsTrue(markers.All(quad => quad.Subject is RdfBlankNode));
            var results = quads.Where(quad => quad.Predicate == new RdfIri("urn:result")).ToArray();
            CollectionAssert.AreEquivalent(new RdfTerm[] { new RdfIri("urn:a"), new RdfIri("urn:c") },
                results.Select(quad => quad.Subject).ToArray());
            foreach (var quad in results)
            {
                var literal = (RdfLiteral)quad.Object;
                Assert.AreEqual("http://www.w3.org/2001/XMLSchema#" + (function == "GROUP_CONCAT" ? "string" : "decimal"), literal.Datatype);
                Assert.AreEqual(1m, Decimal.Parse(literal.LexicalForm, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    [TestMethod]
    [DataRow("SUM", "1/?v", false)]
    [DataRow("SUM", "1/?v", true)]
    [DataRow("AVG", "1/?v", false)]
    [DataRow("AVG", "1/?v", true)]
    [DataRow("GROUP_CONCAT", "STR(1/?v)", false)]
    [DataRow("GROUP_CONCAT", "STR(1/?v)", true)]
    public async Task Sparql_aggregate_expression_errors_are_isolated_between_groups(
        string function, string expression, bool distinct)
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string xsd = "http://www.w3.org/2001/XMLSchema#";
        await database.BulkLoadAsync(Guid.NewGuid(), new[] { ("a", "1"), ("b", "0"), ("b", "1"), ("c", "1") }
            .SelectMany((item, index) => new RdfQuad[]
            {
                new(new RdfIri("urn:input:" + index), new("urn:group"), new RdfLiteral(item.Item1, xsd + "string")),
                new(new RdfIri("urn:input:" + index), new("urn:value"), new RdfLiteral(item.Item2, xsd + "integer"))
            }).ToArray());
        string query = "SELECT ?g (" + function + "(" + (distinct ? "DISTINCT " : "") + expression +
            ") AS ?result) WHERE { ?s <urn:group> ?g ; <urn:value> ?v } GROUP BY ?g ORDER BY ?g";
        var ordinary = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        void Verify(IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows)
        {
            Assert.AreEqual(3, rows.Count);
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, rows.Select(row => ((RdfLiteral)row["g"]).LexicalForm).ToArray());
            Assert.IsFalse(rows[1].ContainsKey("result"));
            Assert.AreEqual(function == "GROUP_CONCAT" ? xsd + "string" : xsd + "decimal",
                ((RdfLiteral)rows[0]["result"]).Datatype);
            Assert.AreEqual(1m, Decimal.Parse(((RdfLiteral)rows[0]["result"]).LexicalForm,
                System.Globalization.CultureInfo.InvariantCulture));
            Assert.AreEqual(rows[0]["result"], rows[2]["result"]);
        }
        Verify(ordinary.Bindings);
        var scanned = new List<IReadOnlyDictionary<string, RdfTerm>>();
        int completions = 0;
        await foreach (var page in RdfSparql.ScanAsync(query, database.QueryPageAsync,
            database.ReleaseQueryContinuationAsync, new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            scanned.AddRange(page.Rows);
            completions += page.Complete ? 1 : 0;
        }
        Verify(scanned);
        Assert.AreEqual(1, completions);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    [DataRow("SUM", "1/?v", false)]
    [DataRow("SUM", "1/?v", true)]
    [DataRow("AVG", "1/?v", false)]
    [DataRow("AVG", "1/?v", true)]
    [DataRow("GROUP_CONCAT", "STR(1/?v)", false)]
    [DataRow("GROUP_CONCAT", "STR(1/?v)", true)]
    [DataRow("SUM", "1/?v", true, "0|0|0|0", 0, null)]
    [DataRow("AVG", "1/?v", true, "0|0|0|0", 0, "0")]
    [DataRow("GROUP_CONCAT", "STR(1/?v)", true, "0|0|0|0", 0, null)]
    public async Task Sparql_aggregate_expression_errors_do_not_become_partial_values(
        string function, string expression, bool distinct, string values = "1|0|2|0", int expectedValid = 2,
        string? expectedResult = null)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.BulkLoadAsync(Guid.NewGuid(), values.Split('|').Select((value, index) =>
            new RdfQuad(new RdfIri("urn:input:" + index), new("urn:value"),
                new RdfLiteral(value,
                    "http://www.w3.org/2001/XMLSchema#integer"))).ToArray());
        string query = "SELECT (" + function + "(" + (distinct ? "DISTINCT " : "") + expression +
            ") AS ?result) (COUNT(" + expression + ") AS ?valid) (COUNT(*) AS ?rows) " +
            "WHERE { ?s <urn:value> ?v }";
        void Verify(IReadOnlyDictionary<string, RdfTerm> row)
        {
            if (expectedResult is null)
            {
                Assert.IsFalse(row.ContainsKey("result"), "An aggregate error must leave its binding absent, not return a partial value.");
            }
            else
            {
                Assert.AreEqual(new RdfLiteral(expectedResult, "http://www.w3.org/2001/XMLSchema#integer"), row["result"]);
            }
            Assert.AreEqual(new RdfLiteral(expectedValid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "http://www.w3.org/2001/XMLSchema#integer"), row["valid"]);
            Assert.AreEqual(new RdfLiteral("4", "http://www.w3.org/2001/XMLSchema#integer"), row["rows"]);
        }
        var ordinary = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        Assert.AreEqual(1, ordinary.Bindings.Count);
        Verify(ordinary.Bindings[0]);
        int rows = 0, completions = 0;
        await foreach (var page in RdfSparql.ScanAsync(query, database.QueryPageAsync,
            database.ReleaseQueryContinuationAsync, new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            foreach (var row in page.Rows)
            {
                Verify(row);
                rows++;
            }
            completions += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(1, rows);
        Assert.AreEqual(1, completions);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "INSERT { <urn:output> <urn:partial> ?result ; <urn:valid> ?valid } WHERE { { " + query + " } }");
        var output = database.Find(new()).Where(quad => quad.Subject == new RdfIri("urn:output")).ToArray();
        var expected = new List<RdfQuad>
        {
            new(new RdfIri("urn:output"), new("urn:valid"), new RdfLiteral(
                expectedValid.ToString(System.Globalization.CultureInfo.InvariantCulture), "http://www.w3.org/2001/XMLSchema#integer"))
        };
        if (expectedResult is not null)
        {
            expected.Add(new(new RdfIri("urn:output"), new("urn:partial"),
                new RdfLiteral(expectedResult, "http://www.w3.org/2001/XMLSchema#integer")));
        }
        CollectionAssert.AreEquivalent(expected.ToArray(), output);
    }
}
