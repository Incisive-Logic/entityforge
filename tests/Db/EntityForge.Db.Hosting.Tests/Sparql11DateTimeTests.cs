using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("YEAR", "2024-02-29T23:45:12.123456789Z", "2024", "integer")]
    [DataRow("MONTH", "2024-02-29T23:45:12Z", "2", "integer")]
    [DataRow("DAY", "2024-02-29T23:45:12Z", "29", "integer")]
    [DataRow("HOURS", "2024-02-29T23:45:12+14:00", "23", "integer")]
    [DataRow("MINUTES", "2024-02-29T23:45:12-05:30", "45", "integer")]
    [DataRow("SECONDS", "2024-02-29T23:45:12.123456789123456789Z", "12.123456789123456789", "decimal")]
    [DataRow("SECONDS", "2024-02-29T23:45:00.000Z", "0", "decimal")]
    [DataRow("TZ", "2024-02-29T23:45:12", "", "string")]
    [DataRow("TZ", "2024-02-29T23:45:12Z", "Z", "string")]
    [DataRow("TZ", "2024-02-29T23:45:12-05:30", "-05:30", "string")]
    [DataRow("TZ", "2024-02-29T23:45:12+00:00", "+00:00", "string")]
    [DataRow("TZ", "2024-02-29T23:45:12-00:00", "-00:00", "string")]
    [DataRow("TIMEZONE", "2024-02-29T23:45:12-05:30", "-PT5H30M", "dayTimeDuration")]
    [DataRow("TIMEZONE", "2024-02-29T23:45:12+00:30", "PT30M", "dayTimeDuration")]
    [DataRow("TIMEZONE", "2024-02-29T23:45:12Z", "PT0S", "dayTimeDuration")]
    [DataRow("YEAR", "-0001-01-01T00:00:00", "-1", "integer")]
    [DataRow("DAY", "-0001-02-29T00:00:00", "29", "integer")]
    [DataRow("YEAR", "-0001-12-31T24:00:00Z", "1", "integer")]
    [DataRow("YEAR", "123456-01-01T00:00:00", "123456", "integer")]
    [DataRow("YEAR", "2024-12-31T24:00:00Z", "2025", "integer")]
    [DataRow("DAY", "2024-02-28T24:00:00Z", "29", "integer")]
    [DataRow("MONTH", "2024-02-29T24:00:00.000Z", "3", "integer")]
    [DataRow("HOURS", "2024-02-29T24:00:00Z", "24", "integer")]
    [DataRow("HOURS", "2024-12-31T24:00:00.000-05:30", "24", "integer")]
    [DataRow("HOURS", "2024-02-29T24:00:00", "24", "integer")]
    public async Task Sparql11_date_time_components_preserve_local_fields_and_precision(string function, string lexical, string expected, string type)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, $"SELECT ({function}('{lexical}'^^<http://www.w3.org/2001/XMLSchema#dateTime>) AS ?x) {{}}").Single();
        Assert.AreEqual(new RdfLiteral(expected, "http://www.w3.org/2001/XMLSchema#" + type), row["x"]);
    }

    [TestMethod]
    [DataRow("2023-02-29T00:00:00")]
    [DataRow("-0004-02-29T00:00:00")]
    [DataRow("1900-02-29T00:00:00")]
    [DataRow("2024-04-31T00:00:00")]
    [DataRow("2024-00-01T00:00:00")]
    [DataRow("2024-13-01T00:00:00")]
    [DataRow("0000-01-01T00:00:00")]
    [DataRow("02024-01-01T00:00:00")]
    [DataRow("2024-01-01T24:00:01")]
    [DataRow("2024-01-01T24:00:00.1")]
    [DataRow("2024-01-01T23:60:00")]
    [DataRow("2024-01-01T23:59:60")]
    [DataRow("2024-01-01T00:00:00+14:01")]
    [DataRow("2024-01-01T00:00:00-15:00")]
    [DataRow("2024-01-01")]
    public async Task Sparql11_invalid_date_time_is_an_expression_error(string lexical)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.AreEqual(0, RdfSparql.Execute(database, $"SELECT (YEAR('{lexical}'^^<http://www.w3.org/2001/XMLSchema#dateTime>) AS ?x) {{}}").Single().Count);
    }

    [TestMethod]
    public async Task Sparql11_missing_timezone_is_distinct_from_zero_timezone()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, "SELECT (COALESCE(TIMEZONE('2024-01-01T00:00:00'^^<http://www.w3.org/2001/XMLSchema#dateTime>),'absent') AS ?x) (YEAR('2024-01-01T00:00:00') AS ?bad) {}").Single();
        Assert.AreEqual(new RdfLiteral("absent"), row["x"]);
        Assert.IsFalse(row.ContainsKey("bad"));
    }

    [TestMethod]
    [DataRow("YEAR")]
    [DataRow("MONTH")]
    [DataRow("DAY")]
    [DataRow("HOURS")]
    [DataRow("MINUTES")]
    [DataRow("SECONDS")]
    [DataRow("TIMEZONE")]
    [DataRow("TZ")]
    public async Task Sparql_date_accessors_enforce_the_dateTime_argument_type(string function)
    {
        await using var database = await OpenAsync(CreateDirectory());
        foreach (string argument in new[]
        {
            "'2024-01-01T00:00:00Z'", "'2024-01-01T00:00:00Z'@en",
            "'2024-01-01'^^<http://www.w3.org/2001/XMLSchema#date>",
            "'invalid'^^<http://www.w3.org/2001/XMLSchema#dateTime>",
            "<urn:timestamp>", "BNODE()", "123", "true", "?absent"
        })
        {
            var row = RdfSparql.Execute(database,
                $"SELECT ({function}({argument}) AS ?bad) (COALESCE({function}({argument}), 'fallback') AS ?recovered) {{}}").Single();
            Assert.IsFalse(row.ContainsKey("bad"), function + "(" + argument + ")");
            Assert.AreEqual(new RdfLiteral("fallback"), row["recovered"]);
        }
    }

    [TestMethod]
    public async Task Sparql_lexical_end_of_day_hour_survives_scan_and_persisted_projection()
    {
        string directory = CreateDirectory();
        const string query = "SELECT (HOURS(?v) AS ?hour) (DAY(?v) AS ?day) { ?s <urn:stamp> ?v }";
        var hour = new RdfLiteral("24", "http://www.w3.org/2001/XMLSchema#integer");
        var day = new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer");
        await using (var database = await OpenAsync(directory))
        {
            await database.CommitAsync(new(Guid.NewGuid(), new RdfMutation[]
            {
                new AssertRdfQuad(new(new RdfIri("urn:s"), new("urn:stamp"),
                    new RdfLiteral("2024-02-29T24:00:00Z", "http://www.w3.org/2001/XMLSchema#dateTime")))
            }));
            var row = RdfSparql.Execute(database, query).Single();
            Assert.AreEqual(hour, row["hour"]);
            Assert.AreEqual(day, row["day"]);
            int rows = 0;
            await foreach (var page in RdfSparql.ScanAsync(query, database.QueryPageAsync,
                database.ReleaseQueryContinuationAsync, new(PageSize: 1)))
            {
                foreach (var scanned in page.Rows)
                {
                    Assert.AreEqual(hour, scanned["hour"]);
                    Assert.AreEqual(day, scanned["day"]);
                    rows++;
                }
            }
            Assert.AreEqual(1, rows);
            Assert.AreEqual(0, database.RetainedQueryContinuationCount);
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
                "INSERT { <urn:projection> <urn:hour> ?hour } WHERE { { " + query + " } }");
            Assert.IsFalse(RdfSparql.Execute(database,
                "SELECT (HOURS('2024-02-29T24:00:01Z'^^<http://www.w3.org/2001/XMLSchema#dateTime>) AS ?bad) {}").Single().ContainsKey("bad"));
        }
        await using var reopened = await OpenAsync(directory);
        Assert.AreEqual(hour, RdfSparql.Execute(reopened,
            "SELECT ?hour { <urn:projection> <urn:hour> ?hour }").Single()["hour"]);
    }
}
